namespace SpaceManager.Models;

public static class FolderNodeAnalysis
{
    /// <summary>
    /// Le nœud est lui-même en cours d'analyse (compte comme « en attente » pour son parent direct).
    /// </summary>
    public static bool IsSelfPending(FolderNode node) =>
        node.IsLoadingChildren || node.IsQueued || node.IsCalculating || node.Size < 0;

    /// <summary>
    /// Au moins un enfant direct est en cours d'analyse.
    /// </summary>
    public static bool HasPendingDirectChild(FolderNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsDummy)
                continue;

            if (IsSelfPending(child))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Affiche le loader « Calcul… » sur cette ligne (dossier ouvert ou parent d'un enfant en analyse).
    /// </summary>
    public static bool ShowsProgress(FolderNode node) =>
        node.IsLoadingChildren || HasPendingDirectChild(node);

    public static void RefreshPendingAnalysis(FolderNode node)
    {
        node.SetHasPendingAnalysis(IsSelfPending(node));
        node.Parent?.NotifyChildAnalysisChanged();
    }
}
