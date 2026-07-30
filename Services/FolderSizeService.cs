using System.IO;
using SpaceManager.Models;

namespace SpaceManager.Services;

public sealed class FolderSizeService
{
    private const int MaxConcurrentScans = 2;

    private readonly Action<Action>? _uiInvoke;
    private readonly SizeCache _sizeCache;
    private readonly SemaphoreSlim _scanSemaphore = new(MaxConcurrentScans, MaxConcurrentScans);
    private readonly Dictionary<string, CancellationTokenSource> _sizeCalculations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public FolderSizeService(Action<Action>? uiInvoke = null, SizeCache? sizeCache = null)
    {
        _uiInvoke = uiInvoke;
        _sizeCache = sizeCache ?? new SizeCache();
    }

    public async Task LoadChildrenAsync(FolderNode node, CancellationToken cancellationToken = default)
    {
        if (!node.IsDirectory || node.ChildrenLoaded || node.IsDummy)
            return;

        node.ChildrenLoaded = true;
        node.Children.Clear();

        IReadOnlyList<(string Path, string Name, bool IsDirectory)> entries;

        try
        {
            entries = await Task.Run(() => EnumerateImmediateChildren(node.FullPath), cancellationToken)
                .ConfigureAwait(true);
        }
        catch
        {
            node.ChildrenLoaded = false;
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var child = new FolderNode(entry.Path, entry.Name, entry.IsDirectory, node);
            node.Children.Add(child);

            if (entry.IsDirectory)
                EnqueueSizeCalculation(child, cancellationToken);
            else
                ApplyFileSize(child);
        }

        node.HasDummyChild = false;
    }

    public Task CalculateSizeAsync(FolderNode node, CancellationToken cancellationToken = default)
    {
        if (!node.IsDirectory || node.IsDummy)
            return Task.CompletedTask;

        EnqueueSizeCalculation(node, cancellationToken);
        return Task.CompletedTask;
    }

    public void CancelAll()
    {
        lock (_lock)
        {
            foreach (var cts in _sizeCalculations.Values)
                cts.Cancel();

            _sizeCalculations.Clear();
        }
    }

    private void EnqueueSizeCalculation(FolderNode node, CancellationToken cancellationToken)
    {
        if (TryApplyCachedSize(node))
            return;

        CancellationTokenSource linkedCts;

        lock (_lock)
        {
            if (_sizeCalculations.TryGetValue(node.FullPath, out var existing))
                existing.Cancel();

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _sizeCalculations[node.FullPath] = linkedCts;
        }

        RunOnUi(() =>
        {
            node.IsQueued = true;
            node.IsCalculating = false;
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _scanSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RunOnUi(() =>
                {
                    node.IsQueued = false;
                    node.IsCalculating = false;
                });
                CleanupCalculation(node.FullPath, linkedCts);
                return;
            }

            if (TryApplyCachedSize(node))
            {
                _scanSemaphore.Release();
                CleanupCalculation(node.FullPath, linkedCts);
                return;
            }

            RunOnUi(() =>
            {
                node.IsQueued = false;
                node.IsCalculating = true;
            });

            try
            {
                var size = await ComputeDirectorySizeAsync(node.FullPath, linkedCts.Token).ConfigureAwait(false);
                _sizeCache.Set(node.FullPath, size);
                var capturedSize = size;
                RunOnUi(() => node.Size = capturedSize);
            }
            catch (OperationCanceledException)
            {
                // Ignoré lors d'un recalcul ou de la fermeture.
            }
            catch
            {
                RunOnUi(() => node.Size = 0);
            }
            finally
            {
                _scanSemaphore.Release();
                RunOnUi(() =>
                {
                    node.IsCalculating = false;
                    node.IsQueued = false;
                });
                CleanupCalculation(node.FullPath, linkedCts);
            }
        }, CancellationToken.None);
    }

    private bool TryApplyCachedSize(FolderNode node)
    {
        if (!_sizeCache.TryGet(node.FullPath, out var size))
            return false;

        RunOnUi(() =>
        {
            node.IsQueued = false;
            node.IsCalculating = false;
            node.Size = size;
        });
        return true;
    }

    private void ApplyFileSize(FolderNode node)
    {
        if (TryApplyCachedSize(node))
            return;

        long size;
        try
        {
            size = new FileInfo(node.FullPath).Length;
        }
        catch
        {
            size = 0;
        }

        _sizeCache.Set(node.FullPath, size);
        node.Size = size;
    }

    private void CleanupCalculation(string fullPath, CancellationTokenSource linkedCts)
    {
        lock (_lock)
        {
            if (_sizeCalculations.TryGetValue(fullPath, out var current) && current == linkedCts)
                _sizeCalculations.Remove(fullPath);
        }

        linkedCts.Dispose();
    }

    private void RunOnUi(Action action)
    {
        if (_uiInvoke != null)
            _uiInvoke(action);
        else
            action();
    }

    private static List<(string Path, string Name, bool IsDirectory)> EnumerateImmediateChildren(string path)
    {
        var result = new List<(string, string, bool)>();

        IEnumerable<string> directories;
        IEnumerable<string> files;

        try
        {
            directories = Directory.EnumerateDirectories(path);
        }
        catch
        {
            directories = [];
        }

        try
        {
            files = Directory.EnumerateFiles(path);
        }
        catch
        {
            files = [];
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(name))
                result.Add((directory, name, true));
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!string.IsNullOrEmpty(name))
                result.Add((file, name, false));
        }

        return result;
    }

    private static async Task<long> ComputeDirectorySizeAsync(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(path);

        while (pendingDirs.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirs.Pop();

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(current);
            }
            catch
            {
                subDirs = [];
            }

            foreach (var subDir in subDirs)
                pendingDirs.Push(subDir);

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // Fichier inaccessible, on continue.
                }

                if (total % (512 * 1024) == 0)
                    await Task.Yield();
            }
        }

        return total;
    }
}
