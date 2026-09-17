using System.Diagnostics;
using System.IO;

namespace SpaceManager.Services;

/// <summary>
/// Liens symboliques, jonctions NTFS et points de montage exposent le contenu d'une autre
/// arborescence. Les suivre compte deux fois les mêmes octets
/// (C:\Users\All Users -> C:\ProgramData) et peut créer des boucles infinies
/// (AppData\Local\Application Data -> AppData\Local).
/// </summary>
public static class ReparsePointGuard
{
    /// <summary>
    /// Jonctions de compatibilité Windows, utilisées en repli quand les attributs sont illisibles.
    /// Ancrées sur la racine d'un lecteur pour ne jamais exclure un vrai dossier homonyme.
    /// </summary>
    private static readonly string[] LegacyCompatibilityPaths =
    [
        @"\Documents and Settings",
        @"\Users\All Users",
        @"\Users\Default User",
        @"\Users\Default Users"
    ];

    /// <summary>
    /// Énumération des sous-dossiers : les points d'analyse sont conservés (ils restent visibles
    /// dans l'arbre) mais <see cref="IsReparsePoint"/> permet de ne pas les parcourir.
    /// Hidden et System doivent rester inclus, sinon ProgramData ou pagefile.sys disparaissent.
    /// </summary>
    public static EnumerationOptions CreateEnumerationOptions() => new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false
    };

    public static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Variante sur un <see cref="DirectoryInfo"/> issu d'une énumération : les attributs sont
    /// déjà en cache, aucun appel disque supplémentaire.
    /// </summary>
    public static bool ShouldSkipDirectory(DirectoryInfo directory)
    {
        try
        {
            if (IsReparsePoint(directory.Attributes))
                return true;
        }
        catch (IOException)
        {
            // Dossier supprimé pendant l'analyse : on retombe sur la liste de compatibilité.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return IsLegacyCompatibilityJunction(directory.FullName);
    }

    public static bool ShouldSkipDirectory(string path)
    {
        try
        {
            return ShouldSkipDirectory(new DirectoryInfo(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Trace DEBUG uniquement : l'attribut Conditional supprime l'appel et l'évaluation de ses
    /// arguments en Release, donc aucun coût sur un scan complet.
    /// </summary>
    [Conditional("DEBUG")]
    public static void LogSkipped(FileSystemInfo info)
    {
        string? target = null;
        try
        {
            target = info.LinkTarget;
        }
        catch
        {
            // La cible n'est pas toujours lisible (ACL, tag propriétaire).
        }

        var suffix = string.IsNullOrEmpty(target) ? string.Empty : $"{Environment.NewLine}-> {target}";
        Debug.WriteLine($"Skipping reparse point:{Environment.NewLine}{info.FullName}{suffix}");
    }

    private static bool IsLegacyCompatibilityJunction(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length < 4 || trimmed[1] != ':')
            return false;

        var withoutDrive = trimmed[2..];

        foreach (var candidate in LegacyCompatibilityPaths)
        {
            if (withoutDrive.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
