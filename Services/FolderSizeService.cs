using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using SpaceManager.Models;

namespace SpaceManager.Services;

public sealed class FolderSizeService
{
    private const int MaxConcurrentScans = 2;
    private const int ChildrenBatchSize = 64;

    private readonly Func<Action, Task>? _uiInvokeAsync;
    private readonly SizeCache _sizeCache;
    private readonly SemaphoreSlim _scanSemaphore = new(MaxConcurrentScans, MaxConcurrentScans);
    private readonly Dictionary<string, CancellationTokenSource> _sizeCalculations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(FolderNode Node, CancellationTokenSource Cts)> _pendingSizeCalculations = new();
    private readonly object _lock = new();
    private int _loadGeneration;
    private int _sizeProcessorCount;

    public FolderSizeService(Func<Action, Task>? uiInvokeAsync = null, SizeCache? sizeCache = null)
    {
        _uiInvokeAsync = uiInvokeAsync;
        _sizeCache = sizeCache ?? new SizeCache();
    }

    public async Task LoadChildrenAsync(
        FolderNode node,
        CancellationToken cancellationToken = default,
        Action<FolderNode>? onBatchAdded = null)
    {
        if (!node.IsDirectory || node.IsDummy || node.ChildrenLoaded || node.IsLoadingChildren)
            return;

        int generation;
        lock (_lock)
            generation = ++_loadGeneration;

        await RunOnUiAsync(() =>
        {
            node.IsLoadingChildren = true;
            node.Children.Clear();
            onBatchAdded?.Invoke(node);
        }).ConfigureAwait(false);

        var batch = new List<(string Path, string Name, bool IsDirectory)>(ChildrenBatchSize);

        try
        {
            await Task.Run(async () =>
            {
                await foreach (var entry in EnumerateImmediateChildrenStreaming(node.FullPath, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    if (IsLoadStale(generation) || CancellationTokenSourceSafe.IsCancellationRequested(cancellationToken))
                        throw new OperationCanceledException();

                    batch.Add(entry);
                    if (batch.Count < ChildrenBatchSize)
                        continue;

                    var copy = batch.ToList();
                    batch.Clear();
                    await AddChildrenBatchAsync(node, copy, cancellationToken, generation, onBatchAdded)
                        .ConfigureAwait(false);
                }

                if (batch.Count > 0)
                    await AddChildrenBatchAsync(node, batch, cancellationToken, generation, onBatchAdded)
                        .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            if (IsLoadStale(generation) || CancellationTokenSourceSafe.IsCancellationRequested(cancellationToken))
                throw new OperationCanceledException();

            await RunOnUiAsync(() =>
            {
                node.HasDummyChild = false;
                node.ChildrenLoaded = true;
                node.IsLoadingChildren = false;
                onBatchAdded?.Invoke(node);
            }).ConfigureAwait(false);

            StartSizeCalculationsForNode(node, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() => node.IsLoadingChildren = false).ConfigureAwait(false);
            throw;
        }
        catch (ObjectDisposedException)
        {
            await RunOnUiAsync(() => node.IsLoadingChildren = false).ConfigureAwait(false);
            throw new OperationCanceledException();
        }
        catch
        {
            await RunOnUiAsync(() =>
            {
                node.IsLoadingChildren = false;
                node.ChildrenLoaded = false;
            }).ConfigureAwait(false);
        }
    }

    public Task CalculateSizeAsync(FolderNode node, CancellationToken cancellationToken = default)
    {
        if (!node.IsDirectory || node.IsDummy)
            return Task.CompletedTask;

        QueueSizeCalculation(node, cancellationToken);
        return Task.CompletedTask;
    }

    public void CancelAll()
    {
        List<CancellationTokenSource> toDisposePending;

        lock (_lock)
        {
            _loadGeneration++;

            toDisposePending = new List<CancellationTokenSource>();
            while (_pendingSizeCalculations.TryDequeue(out var pending))
            {
                CancellationTokenSourceSafe.Cancel(pending.Cts);
                toDisposePending.Add(pending.Cts);
            }

            foreach (var cts in _sizeCalculations.Values)
                CancellationTokenSourceSafe.Cancel(cts);

            _sizeCalculations.Clear();
        }

        foreach (var cts in toDisposePending)
            CancellationTokenSourceSafe.Dispose(cts);
    }

    private async Task AddChildrenBatchAsync(
        FolderNode node,
        IReadOnlyList<(string Path, string Name, bool IsDirectory)> batch,
        CancellationToken cancellationToken,
        int generation,
        Action<FolderNode>? onBatchAdded)
    {
        if (CancellationTokenSourceSafe.IsCancellationRequested(cancellationToken) || IsLoadStale(generation))
            return;

        await RunOnUiAsync(() =>
        {
            if (CancellationTokenSourceSafe.IsCancellationRequested(cancellationToken) || IsLoadStale(generation))
                return;

            foreach (var entry in batch)
            {
                if (CancellationTokenSourceSafe.IsCancellationRequested(cancellationToken) || IsLoadStale(generation))
                    return;

                var child = new FolderNode(entry.Path, entry.Name, entry.IsDirectory, node);
                node.Children.Add(child);

                if (entry.IsDirectory)
                    child.IsQueued = true;
                else
                    ApplyFileSize(child);
            }

            onBatchAdded?.Invoke(node);
        }).ConfigureAwait(false);
    }

    private void StartSizeCalculationsForNode(FolderNode node, CancellationToken cancellationToken)
    {
        foreach (var child in node.Children.Where(c => !c.IsDummy && c.IsDirectory).Take(TreeRowBuilder.MaxVisibleChildren))
            QueueSizeCalculation(child, cancellationToken);
    }

    private void QueueSizeCalculation(FolderNode node, CancellationToken cancellationToken)
    {
        if (TryApplyCachedSize(node))
            return;

        CancellationTokenSource linkedCts;

        lock (_lock)
        {
            if (_sizeCalculations.TryGetValue(node.FullPath, out var existing))
                CancellationTokenSourceSafe.Cancel(existing);

            try
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _sizeCalculations[node.FullPath] = linkedCts;
        }

        _pendingSizeCalculations.Enqueue((node, linkedCts));
        EnsureSizeProcessorRunning();
    }

    private void EnsureSizeProcessorRunning()
    {
        if (Interlocked.CompareExchange(ref _sizeProcessorCount, 1, 0) != 0)
            return;

        _ = Task.Run(ProcessSizeCalculationQueueAsync);
    }

    private async Task ProcessSizeCalculationQueueAsync()
    {
        try
        {
            while (_pendingSizeCalculations.TryDequeue(out var work))
            {
                await ProcessOneSizeCalculationAsync(work.Node, work.Cts).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _sizeProcessorCount, 0);
            if (!_pendingSizeCalculations.IsEmpty)
                EnsureSizeProcessorRunning();
        }
    }

    private async Task ProcessOneSizeCalculationAsync(FolderNode node, CancellationTokenSource linkedCts)
    {
        var cancellationToken = linkedCts.Token;

        try
        {
            try
            {
                await _scanSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await RunOnUiAsync(() =>
                {
                    node.IsQueued = false;
                    node.IsCalculating = false;
                }).ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (TryApplyCachedSize(node))
            {
                _scanSemaphore.Release();
                return;
            }

            await RunOnUiAsync(() =>
            {
                node.IsQueued = false;
                node.IsCalculating = true;
            }).ConfigureAwait(false);

            try
            {
                var size = await ComputeDirectorySizeAsync(node.FullPath, cancellationToken).ConfigureAwait(false);
                _sizeCache.Set(node.FullPath, size);
                var capturedSize = size;
                await RunOnUiAsync(() => node.Size = capturedSize).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignoré lors d'un recalcul ou de la fermeture.
            }
            catch (ObjectDisposedException)
            {
                // Source d'annulation déjà libérée.
            }
            catch
            {
                await RunOnUiAsync(() => node.Size = 0).ConfigureAwait(false);
            }
            finally
            {
                _scanSemaphore.Release();
                await RunOnUiAsync(() =>
                {
                    node.IsCalculating = false;
                    node.IsQueued = false;
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            CleanupCalculation(node.FullPath, linkedCts);
        }
    }

    private bool TryApplyCachedSize(FolderNode node)
    {
        if (!_sizeCache.TryGet(node.FullPath, out var size))
            return false;

        RunOnUiSync(() =>
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

        CancellationTokenSourceSafe.Dispose(linkedCts);
    }

    private bool IsLoadStale(int generation)
    {
        lock (_lock)
            return generation != _loadGeneration;
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_uiInvokeAsync != null)
            return _uiInvokeAsync(action);

        action();
        return Task.CompletedTask;
    }

    private void RunOnUiSync(Action action)
    {
        if (_uiInvokeAsync != null)
            _uiInvokeAsync(action).GetAwaiter().GetResult();
        else
            action();
    }

    private static void ThrowIfCancellationRequestedSafe(CancellationToken cancellationToken)
    {
        if (CancellationTokenSourceSafe.IsCancellationRequested(cancellationToken))
            throw new OperationCanceledException();
    }

    private static async IAsyncEnumerable<(string Path, string Name, bool IsDirectory)> EnumerateImmediateChildrenStreaming(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

        var yielded = 0;

        foreach (var directory in directories)
        {
            ThrowIfCancellationRequestedSafe(cancellationToken);

            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                continue;

            yield return (directory, name, true);

            if (++yielded % 256 == 0)
                await Task.Yield();
        }

        foreach (var file in files)
        {
            ThrowIfCancellationRequestedSafe(cancellationToken);

            var name = Path.GetFileName(file);
            if (string.IsNullOrEmpty(name))
                continue;

            yield return (file, name, false);

            if (++yielded % 256 == 0)
                await Task.Yield();
        }
    }

    private static async Task<long> ComputeDirectorySizeAsync(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(path);

        while (pendingDirs.Count > 0)
        {
            ThrowIfCancellationRequestedSafe(cancellationToken);
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
                ThrowIfCancellationRequestedSafe(cancellationToken);

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
