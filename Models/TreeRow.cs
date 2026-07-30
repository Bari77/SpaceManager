namespace SpaceManager.Models;

public sealed class TreeRow(FolderNode node, int depth)
{
    public FolderNode Node { get; } = node;
    public int Depth { get; } = depth;

    public bool CanExpand => Node.IsDirectory && !Node.IsDummy;
}
