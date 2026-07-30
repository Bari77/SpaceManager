using System.IO;

namespace SpaceManager.Services;

public sealed class SizeCache
{
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public bool TryGet(string path, out long size)
    {
        lock (_lock)
            return _sizes.TryGetValue(NormalizeKey(path), out size);
    }

    public void Set(string path, long size)
    {
        lock (_lock)
            _sizes[NormalizeKey(path)] = size;
    }

    private static string NormalizeKey(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (trimmed.Length == 2 && trimmed[1] == ':')
                return trimmed;

            if (Directory.Exists(fullPath) || path.EndsWith('\\') || path.EndsWith('/'))
                return trimmed;

            return fullPath;
        }
        catch
        {
            return path;
        }
    }
}
