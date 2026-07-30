using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace SpaceManager.Services;

public sealed record UpdateInfo(Version Version, string ReleaseUrl, string? DownloadUrl, string ReleaseNotes);

public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Bari77/SpaceManager/releases/latest";

    public static Version CurrentVersion => GetCurrentVersion();

    public static async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"SpaceManager/{CurrentVersion}");

            var response = await client.GetFromJsonAsync<GitHubRelease>(LatestReleaseApi, cancellationToken)
                .ConfigureAwait(false);

            if (response == null || !TryParseVersion(response.TagName, out var latestVersion))
                return null;

            if (latestVersion <= CurrentVersion)
                return null;

            var downloadUrl = response.Assets?
                .FirstOrDefault(a => string.Equals(a.Name, "SpaceManager.exe", StringComparison.OrdinalIgnoreCase))
                ?.BrowserDownloadUrl
                ?? response.Assets?
                    .FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ?.BrowserDownloadUrl;

            return new UpdateInfo(
                latestVersion,
                response.HtmlUrl ?? "https://github.com/Bari77/SpaceManager/releases/latest",
                downloadUrl,
                response.Body ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informational, out var version))
            return version;

        return assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim().TrimStart('v', 'V');
        var plusIndex = trimmed.IndexOf('+');
        if (plusIndex >= 0)
            trimmed = trimmed[..plusIndex];

        if (Version.TryParse(trimmed, out var parsed))
        {
            version = parsed;
            return true;
        }

        return false;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
