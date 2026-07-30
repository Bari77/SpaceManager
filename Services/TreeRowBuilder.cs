using SpaceManager.Models;

namespace SpaceManager.Services;

public static class TreeRowBuilder
{
    private const int MinSkeletonRowsWhileLoading = 10;
    public const int MaxVisibleChildrenWhileLoading = 150;
    public const int MaxVisibleChildren = 2000;

    public static List<TreeRow> Build(FolderNode? root)
    {
        var rows = new List<TreeRow>();
        if (root == null || root.IsDummy)
            return rows;

        AppendNode(root, depth: 0, rows);
        return rows;
    }

    private static void AppendNode(FolderNode node, int depth, IList<TreeRow> rows)
    {
        if (node.IsDummy)
            return;

        rows.Add(TreeRow.FromNode(node, depth));

        if (!node.IsDirectory || !node.IsExpanded)
            return;

        var total = AppendChildren(node, depth, rows);

        if (node.IsLoadingChildren)
        {
            var skeletonCount = Math.Max(2, MinSkeletonRowsWhileLoading - Math.Min(total, MaxVisibleChildrenWhileLoading));
            for (var i = 0; i < skeletonCount; i++)
                rows.Add(TreeRow.CreateSkeleton(depth + 1));
        }
    }

    private static int AppendChildren(FolderNode node, int depth, IList<TreeRow> rows)
    {
        var maxVisible = node.IsLoadingChildren ? MaxVisibleChildrenWhileLoading : MaxVisibleChildren;
        var shown = 0;

        foreach (var child in node.Children)
        {
            if (child.IsDummy)
                continue;

            if (shown < maxVisible)
            {
                AppendNode(child, depth + 1, rows);
                shown++;
                continue;
            }

            if (node.IsLoadingChildren)
                break;
        }

        var total = node.IsLoadingChildren
            ? node.Children.Count
            : CountNonDummyChildren(node);

        if (total > maxVisible)
            rows.Add(TreeRow.CreateOverflow(depth + 1, total - maxVisible));

        return total;
    }

    private static int CountNonDummyChildren(FolderNode node)
    {
        var count = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsDummy)
                count++;
        }

        return count;
    }
}
