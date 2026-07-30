namespace SpaceManager.Services;

internal static class CancellationTokenSourceSafe
{
    public static bool IsCancellationRequested(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return false;

        try
        {
            return cancellationToken.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    public static void Cancel(CancellationTokenSource? cts)
    {
        if (cts == null)
            return;

        try
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Déjà libéré.
        }
    }

    public static void Dispose(CancellationTokenSource? cts)
    {
        if (cts == null)
            return;

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Déjà libéré.
        }
    }
}
