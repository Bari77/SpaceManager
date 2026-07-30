using System.IO;

namespace SpaceManager.Services;

public static class PathHelper
{
    public static string NormalizeDirectoryPath(string path)
    {
        var trimmed = path.Trim().Trim('"');

        // C: sans séparateur = répertoire courant du lecteur (souvent System32 au démarrage).
        if (TryGetDriveRoot(trimmed, out var driveRoot))
            return driveRoot;

        var fullPath = Path.GetFullPath(trimmed);

        if (fullPath.Length == 2 && fullPath[1] == ':')
            return fullPath + Path.DirectorySeparatorChar;

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Détecte C:, C:\ ou C:/ et renvoie toujours C:\ (sans passer par GetFullPath).
    /// </summary>
    public static bool TryGetDriveRoot(string path, out string driveRoot)
    {
        driveRoot = string.Empty;
        if (path.Length < 2 || path[1] != ':')
            return false;

        var letter = char.ToUpperInvariant(path[0]);
        if (letter is < 'A' or > 'Z')
            return false;

        if (path.Length == 2 || (path.Length == 3 && (path[2] == '\\' || path[2] == '/')))
        {
            driveRoot = letter + ":\\";
            return true;
        }

        return false;
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
