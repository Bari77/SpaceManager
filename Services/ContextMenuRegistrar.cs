using Microsoft.Win32;

namespace SpaceManager.Services;

public static class ContextMenuRegistrar
{
    private const string MenuKeyName = "SpaceManager";
    private const string MenuLabel = "Analyser l'espace disque avec SpaceManager";

    private static readonly (string SubKey, string Argument)[] MenuTargets =
    [
        (@"Directory\shell\" + MenuKeyName, "\"%1\""),
        (@"Directory\Background\shell\" + MenuKeyName, "\"%V\""),
        (@"Drive\shell\" + MenuKeyName, "\"%1\"")
    ];

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{MenuTargets[0].SubKey}");
        return key != null;
    }

    public static void Register(string executablePath)
    {
        var quotedExecutable = Quote(executablePath);

        foreach (var (subKey, argument) in MenuTargets)
        {
            using var menuKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{subKey}");
            menuKey?.SetValue(string.Empty, MenuLabel);
            menuKey?.SetValue("Icon", $"{quotedExecutable},0");

            using var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{subKey}\command");
            commandKey?.SetValue(string.Empty, $"{quotedExecutable} {argument}");
        }
    }

    public static void EnsureRegistered()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        Register(executablePath);
    }

    public static void Unregister()
    {
        foreach (var (subKey, _) in MenuTargets)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{subKey}", throwOnMissingSubKey: false);
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
