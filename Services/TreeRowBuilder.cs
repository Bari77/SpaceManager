using SpaceManager.Models;

namespace SpaceManager.Services;

public static class TreeRowBuilder
{
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

        rows.Add(new TreeRow(node, depth));

        if (!node.IsDirectory || !node.IsExpanded)
            return;

        foreach (var child in node.Children)
            AppendNode(child, depth + 1, rows);
    }
}
