using System.IO;
using System.Windows;
using SpaceManager.Services;

namespace SpaceManager;

public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private readonly CancellationTokenSource _cancellation = new();

    public UpdateWindow(UpdateInfo update)
    {
        _update = update;
        InitializeComponent();
        VersionText.Text = $"Version {update.Version.ToString(3)}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_update.DownloadUrl))
                throw new InvalidOperationException("URL de téléchargement indisponible.");

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"SpaceManager-{Guid.NewGuid():N}.exe");

            StatusText.Text = "Téléchargement en cours…";
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 0;

            var progress = new Progress<double>(value =>
            {
                ProgressBar.Value = value * 100;
                ProgressPercentText.Text = $"{value * 100:0}%";
            });

            await UpdateInstaller.DownloadAsync(
                _update.DownloadUrl,
                tempPath,
                progress,
                _cancellation.Token).ConfigureAwait(true);

            StatusText.Text = "Installation en cours…";
            ProgressBar.IsIndeterminate = true;
            ProgressPercentText.Text = string.Empty;
            await Task.Delay(400).ConfigureAwait(true);

            var targetPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Impossible de déterminer le chemin de l'application.");

            UpdateInstaller.ScheduleReplaceAndRestart(
                tempPath,
                targetPath,
                Environment.ProcessId,
                _update.Version);

            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"La mise à jour a échoué :\n{ex.Message}",
                "SpaceManager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation.Cancel();
        base.OnClosed(e);
    }
}
