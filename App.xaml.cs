using System.IO;
using System.Windows;
using SpaceManager.Services;

namespace SpaceManager;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Une erreur inattendue s'est produite :\n{args.Exception.Message}",
                "SpaceManager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                    MessageBox.Show(
                        $"Une erreur fatale s'est produite :\n{ex.Message}",
                        "SpaceManager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error));
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Dispatcher.BeginInvoke(() =>
                MessageBox.Show(
                    $"Erreur lors de l'analyse :\n{args.Exception.InnerException?.Message ?? args.Exception.Message}",
                    "SpaceManager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));
            args.SetObserved();
        };

        TryEnsureContextMenuRegistered();

        var startupPath = ResolveStartupPath(e.Args);
        _singleInstance = SingleInstanceService.Acquire();

        if (!_singleInstance.IsFirstInstance)
        {
            if (SingleInstanceService.TrySendPathToRunningInstance(startupPath))
            {
                Shutdown();
                return;
            }
        }

        var mainWindow = new MainWindow(startupPath);
        MainWindow = mainWindow;

        if (_singleInstance.IsFirstInstance)
            _singleInstance.StartListening(path => ((MainWindow)MainWindow!).OpenPath(path));

        mainWindow.Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string ResolveStartupPath(string[] args)
    {
        if (args.Length > 0)
        {
            var candidate = args[0].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(candidate))
                return GetDefaultPath();

            try
            {
                if (PathHelper.TryGetDriveRoot(candidate, out _))
                    return PathHelper.NormalizeDirectoryPath(candidate);

                if (Directory.Exists(candidate) || Directory.Exists(candidate.TrimEnd('\\') + "\\"))
                    return PathHelper.NormalizeDirectoryPath(candidate);

                if (File.Exists(candidate))
                    return PathHelper.NormalizeDirectoryPath(Path.GetDirectoryName(Path.GetFullPath(candidate)) ?? candidate);
            }
            catch
            {
                // Chemin invalide, on retombe sur le dossier utilisateur.
            }
        }

        return GetDefaultPath();
    }

    private static string GetDefaultPath() =>
        PathHelper.NormalizeDirectoryPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static void TryEnsureContextMenuRegistered()
    {
        try
        {
            ContextMenuRegistrar.EnsureRegistered();
        }
        catch
        {
            // Ne pas bloquer le démarrage si l'enregistrement échoue.
        }
    }
}
