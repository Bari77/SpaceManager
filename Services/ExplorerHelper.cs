using System.Diagnostics;
using System.IO;

namespace SpaceManager.Services;

public static class ExplorerHelper
{
    public static void OpenFolder(string path)
    {
        var folderPath = Directory.Exists(path)
            ? PathHelper.NormalizeDirectoryPath(path)
            : PathHelper.NormalizeDirectoryPath(Path.GetDirectoryName(path) ?? path);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folderPath.TrimEnd('\\')}\"",
            UseShellExecute = true
        });
    }

    public static void OpenContainingFolder(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        OpenFolder(path);
    }
}
