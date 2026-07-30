using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace SpaceManager.Services;

public static class UpdateInstaller
{
    public static bool CanUpdateInPlace()
    {
        var path = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(path)
               && File.Exists(path)
               && path.EndsWith("SpaceManager.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task DownloadAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateHttpClient();
        using var response = await client
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var fileStream = File.Create(destinationPath);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;

        while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesRead += read;

            if (totalBytes is > 0)
                progress?.Report(bytesRead / (double)totalBytes.Value);
        }

        progress?.Report(1);
    }

    public static void ScheduleReplaceAndRestart(
        string downloadedExePath,
        string targetExePath,
        int processId,
        Version newVersion)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"SpaceManager-update-{Guid.NewGuid():N}.ps1");
        var versionText = newVersion.ToString(3);

        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {{processId}}
            Start-Sleep -Milliseconds 800
            $source = '{{EscapePowerShellLiteral(downloadedExePath)}}'
            $target = '{{EscapePowerShellLiteral(targetExePath)}}'
            $script = '{{EscapePowerShellLiteral(scriptPath)}}'
            for ($i = 0; $i -lt 40; $i++) {
                try {
                    Copy-Item -LiteralPath $source -Destination $target -Force
                    break
                } catch {}
                Start-Sleep -Milliseconds 500
            }
            Start-Process -FilePath $target -ArgumentList '--updated','{{versionText}}'
            Remove-Item -LiteralPath $source -Force
            Remove-Item -LiteralPath $script -Force
            """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("SpaceManager", UpdateChecker.CurrentVersion.ToString(3)));
        return client;
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''");
}
