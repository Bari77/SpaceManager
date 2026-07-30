using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace SpaceManager.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = "Global\\SpaceManager_SingleInstance";
    private const string PipeName = "SpaceManager_SingleInstance_Pipe";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _pipeCancellation;
    private Task? _pipeTask;

    private SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
    }

    public bool IsFirstInstance { get; }

    public static SingleInstanceService Acquire() => new();

    public void StartListening(Action<string> onPathReceived)
    {
        if (!IsFirstInstance)
            return;

        _pipeCancellation = new CancellationTokenSource();
        var token = _pipeCancellation.Token;

        _pipeTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using var reader = new StreamReader(server, Encoding.UTF8);
                var path = await reader.ReadToEndAsync(token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(path))
                {
                    var normalized = path.Trim();
                    Application.Current?.Dispatcher.BeginInvoke(() => onPathReceived(normalized));
                }
            }
        }, token);
    }

    public static bool TrySendPathToRunningInstance(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.Write(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _pipeCancellation?.Cancel();

        try
        {
            _pipeTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignoré à la fermeture.
        }

        _pipeCancellation?.Dispose();

        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignoré si déjà libéré.
            }
        }

        _mutex.Dispose();
    }
}
