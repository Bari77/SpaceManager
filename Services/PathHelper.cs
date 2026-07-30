using System.IO;

namespace SpaceManager.Services;

public static class PathHelper
{
    public static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (fullPath.Length == 2 && fullPath[1] == ':')
            return fullPath + Path.DirectorySeparatorChar;

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    public static bool IsDriveRoot(string path)
    {
        var normalized = NormalizeDirectoryPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized.Length == 2 && normalized[1] == ':';
    }

    public static string GetDisplayName(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed;

        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? fullPath : name;
    }
}
