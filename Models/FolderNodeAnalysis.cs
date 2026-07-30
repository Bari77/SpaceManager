namespace SpaceManager.Models;

public static class FolderNodeAnalysis
{
    public static void RefreshPendingAnalysis(FolderNode node) =>
        node.SetHasPendingAnalysis(ComputePendingAnalysis(node));

    private static bool ComputePendingAnalysis(FolderNode node) =>
        node.IsLoadingChildren || node.IsQueued || node.IsCalculating || node.Size < 0;
}
