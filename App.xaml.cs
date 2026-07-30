using System.Diagnostics;
using System.IO;
using System.Windows;
using SpaceManager.Services;

namespace SpaceManager;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private string? _justUpdatedVersion;

    protected override void OnStartup(StartupEventArgs e)
    {
        _justUpdatedVersion = TryReadUpdatedVersionArg(e.Args);

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

        if (_justUpdatedVersion != null)
            ShowUpdateSuccessMessage(_justUpdatedVersion);

        if (_singleInstance.IsFirstInstance && _justUpdatedVersion == null)
            TryPromptForUpdateAsync();

        base.OnStartup(e);
    }

    private static void ShowUpdateSuccessMessage(string version)
    {
        MessageBox.Show(
            $"SpaceManager a été mis à jour vers la version {version}.\n\nL'application est prête.",
            "Mise à jour réussie",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string? TryReadUpdatedVersionArg(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--updated", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1].Trim();
        }

        return null;
    }

    private static void TryPromptForUpdateAsync()
    {
        _ = Task.Run(async () =>
        {
            var update = await UpdateChecker.CheckForUpdateAsync().ConfigureAwait(false);
            if (update == null)
                return;

            await Current.Dispatcher.InvokeAsync(() =>
            {
                if (Current.MainWindow == null)
                    return;

                var current = UpdateChecker.CurrentVersion.ToString(3);
                var latest = update.Version.ToString(3);
                var message =
                    $"Une nouvelle version est disponible.\n\n" +
                    $"Version installée : {current}\n" +
                    $"Dernière version : {latest}\n\n" +
                    "Voulez-vous télécharger et installer la mise à jour maintenant ?";

                var result = MessageBox.Show(
                    message,
                    "Mise à jour SpaceManager",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.No,
                    MessageBoxOptions.DefaultDesktopOnly);

                if (result != MessageBoxResult.Yes)
                    return;

                if (string.IsNullOrWhiteSpace(update.DownloadUrl))
                {
                    OpenBrowser(update.ReleaseUrl);
                    return;
                }

                if (!UpdateInstaller.CanUpdateInPlace())
                {
                    MessageBox.Show(
                        "La mise à jour automatique n'est disponible que pour l'exécutable publié SpaceManager.exe.\n\n" +
                        "Le navigateur va s'ouvrir pour télécharger la nouvelle version.",
                        "SpaceManager",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    OpenBrowser(update.DownloadUrl ?? update.ReleaseUrl);
                    return;
                }

                var mainWindow = Current.MainWindow;
                mainWindow!.Visibility = Visibility.Hidden;
                try
                {
                    var dialog = new UpdateWindow(update)
                    {
                        WindowStartupLocation = WindowStartupLocation.CenterScreen
                    };
                    dialog.ShowDialog();
                }
                finally
                {
                    mainWindow.Visibility = Visibility.Visible;
                }
            });
        });
    }

    private static void OpenBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static string ResolveStartupPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--updated", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            var candidate = args[i].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("--", StringComparison.Ordinal))
                continue;

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
                // Chemin invalide, on passe au prochain argument.
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
