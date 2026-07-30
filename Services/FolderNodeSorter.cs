using SpaceManager.Models;

namespace SpaceManager.Services;

public static class FolderNodeSorter
{
    public static void SortRecursive(FolderNode node, SortColumn column, SortDirection direction)
    {
        SortChildren(node, column, direction);

        foreach (var child in node.Children.Where(c => !c.IsDummy && c.IsDirectory && c.ChildrenLoaded))
            SortRecursive(child, column, direction);
    }

    public static void SortChildren(FolderNode parent, SortColumn column, SortDirection direction)
    {
        if (!parent.ChildrenLoaded || parent.Children.Count == 0)
            return;

        var items = parent.Children.Where(c => !c.IsDummy).ToList();
        if (items.Count == 0)
            return;

        var sorted = column switch
        {
            SortColumn.Name => direction == SortDirection.Ascending
                ? items.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase),
            SortColumn.Size => direction == SortDirection.Ascending
                ? items.OrderBy(c => c.Size < 0).ThenBy(c => c.Size)
                : items.OrderBy(c => c.Size < 0).ThenByDescending(c => c.Size),
            _ => items.AsEnumerable()
        };

        parent.Children.Clear();
        foreach (var item in sorted)
            parent.Children.Add(item);

        UpdateSizeRatios(parent);
    }

    public static void UpdateSizeRatios(FolderNode parent)
    {
        var siblings = parent.Children.Where(c => !c.IsDummy).ToList();
        var knownSizes = siblings.Where(c => c.Size >= 0).Select(c => c.Size).ToList();
        var maxSize = knownSizes.Count > 0 ? knownSizes.Max() : 0L;

        foreach (var child in siblings)
        {
            if (child.Size < 0 || maxSize == 0)
                child.SizeRatio = 0;
            else
                child.SizeRatio = (double)child.Size / maxSize;
        }
    }
}
