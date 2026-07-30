namespace SpaceManager.Models;

public sealed class TreeRow
{
    private TreeRow(FolderNode? node, int depth, bool isSkeleton, bool isOverflow, string? overflowText)
    {
        Node = node;
        Depth = depth;
        IsSkeleton = isSkeleton;
        IsOverflow = isOverflow;
        OverflowText = overflowText;
    }

    public FolderNode? Node { get; }
    public int Depth { get; }
    public bool IsSkeleton { get; }
    public bool IsOverflow { get; }
    public string? OverflowText { get; }

    public bool CanExpand => !IsSkeleton && !IsOverflow && Node is { IsDirectory: true, IsDummy: false };

    public static TreeRow FromNode(FolderNode node, int depth) =>
        new(node, depth, isSkeleton: false, isOverflow: false, overflowText: null);

    public static TreeRow CreateSkeleton(int depth) =>
        new(node: null, depth, isSkeleton: true, isOverflow: false, overflowText: null);

    public static TreeRow CreateOverflow(int depth, int hiddenCount) =>
        new(node: null, depth, isSkeleton: false, isOverflow: true, overflowText: $"... et {hiddenCount:N0} autres éléments");
}
