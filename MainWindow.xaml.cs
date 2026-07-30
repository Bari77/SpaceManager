using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SpaceManager.Models;
using SpaceManager.Services;

namespace SpaceManager;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _navigationCancellation;
    private readonly HashSet<FolderNode> _trackedNodes = [];
    private readonly FolderSizeService _folderSizeService;
    private readonly DispatcherTimer _refreshDebounceTimer;
    private readonly DispatcherTimer _loadingRowsTimer;
    private FolderNode? _rootNode;
    private SortColumn _sortColumn = SortColumn.Size;
    private SortDirection _sortDirection = SortDirection.Descending;
    private bool _isNavigating;
    private bool _suspendRefresh;
    private bool _refreshPending;

    public MainWindow(string initialPath)
    {
        _folderSizeService = new FolderSizeService(MarshalToUiAsync);
        _refreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshDebounceTimer.Tick += RefreshDebounceTimerOnTick;
        _loadingRowsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _loadingRowsTimer.Tick += LoadingRowsTimerOnTick;

        InitializeComponent();
        SetApplicationIcon();
        ConfigureRowContextMenu();
        ApplyApplicationVersion();
        DataContext = this;
        UpdateSortHeaders();
        LoadPath(initialPath);
    }

    public ObservableCollection<TreeRow> Rows { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ConfigureRowContextMenu()
    {
        var baseStyle = (Style)FindResource(typeof(ListViewItem));
        var rowStyle = new Style(typeof(ListViewItem), baseStyle);

        var contextMenu = new ContextMenu();
        var copyItem = new MenuItem { Header = "Copier le chemin" };
        copyItem.Click += CopyPathMenuItem_Click;
        var openItem = new MenuItem { Header = "Ouvrir le dossier" };
        openItem.Click += OpenInExplorerMenuItem_Click;
        contextMenu.Items.Add(copyItem);
        contextMenu.Items.Add(openItem);

        rowStyle.Setters.Add(new Setter(ContextMenuProperty, contextMenu));
        rowStyle.Setters.Add(new EventSetter(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(ListViewItem_ContextMenuOpening)));

        FolderList.ItemContainerStyle = rowStyle;
    }

    private void ApplyApplicationVersion()
    {
        var version = UpdateChecker.CurrentVersion.ToString(3);
        Title = $"SpaceManager v{version}";
        AppVersionText.Text = $"v{version}";
    }

    private void SetApplicationIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        }
        catch
        {
            try
            {
                Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/space-manager-logo.png", UriKind.Absolute));
            }
            catch
            {
                // L'icône embarquée dans l'exécutable reste utilisée par Windows.
            }
        }
    }

    private Task MarshalToUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }

    private void MarshalToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    public void OpenPath(string path)
    {
        Activate();
        Focus();
        LoadPath(path);
    }

    private void LoadPath(string path)
    {
        CancelCurrentNavigation();
        _folderSizeService.CancelAll();
        _refreshDebounceTimer.Stop();
        _loadingRowsTimer.Stop();
        _suspendRefresh = false;
        _refreshPending = false;

        DetachAllTrackedNodes();

        Rows.Clear();

        var fullPath = PathHelper.NormalizeDirectoryPath(path);
        _isNavigating = true;
        PathTextBox.Text = fullPath.TrimEnd('\\');
        _isNavigating = false;

        _rootNode = new FolderNode(fullPath, PathHelper.GetDisplayName(fullPath), isDirectory: true);
        _rootNode.SizeRatio = 1;
        TrackNode(_rootNode);

        _navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_windowCancellation.Token);
        var rootNode = _rootNode;
        var navigationToken = _navigationCancellation.Token;

        RebuildRows();
        _ = ExpandRootAsync(rootNode, navigationToken);
    }

    private void CancelCurrentNavigation(bool dispose = false)
    {
        var cts = _navigationCancellation;
        _navigationCancellation = null;
        if (cts == null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Déjà annulé ou libéré par une autre opération.
        }

        if (dispose)
            CancellationTokenSourceSafe.Dispose(cts);
    }

    private void DetachAllTrackedNodes()
    {
        foreach (var node in _trackedNodes)
        {
            node.PropertyChanged -= NodeOnPropertyChanged;
            node.SizeChanged -= NodeOnSizeChanged;
            node.IsExpandedChanged -= NodeOnIsExpandedChanged;
        }

        _trackedNodes.Clear();
    }

    private bool IsNodeInCurrentTree(FolderNode node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current == _rootNode)
                return true;
        }

        return false;
    }

    private CancellationToken GetNavigationToken() =>
        _navigationCancellation?.Token ?? _windowCancellation.Token;

    private async Task ExpandRootAsync(FolderNode rootNode, CancellationToken cancellationToken)
    {
        try
        {
            rootNode.IsExpanded = true;
            if (cancellationToken.IsCancellationRequested)
                return;

            RebuildRows();
            await LoadAndPresentAsync(rootNode, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || _rootNode != rootNode)
                return;

            await MarshalToUiAsync(() =>
            {
                UpdateRootSizeFromChildren();
                UpdateRootSizeText();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Fermeture ou changement de dossier en cours.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Erreur lors de l'analyse du dossier :\n{ex.Message}",
                "SpaceManager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task LoadAndPresentAsync(FolderNode node, CancellationToken cancellationToken)
    {
        _suspendRefresh = true;
        try
        {
            await _folderSizeService.LoadChildrenAsync(
                node,
                cancellationToken,
                OnChildrenBatchLoaded).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || !IsNodeInCurrentTree(node))
                return;

            await MarshalToUiAsync(() =>
            {
                var childCount = 0;
                foreach (var child in node.Children)
                {
                    if (!child.IsDummy)
                        childCount++;
                }

                if (childCount <= TreeRowBuilder.MaxVisibleChildren)
                {
                    ApplySort(node);
                    FolderNodeSorter.UpdateSizeRatios(node);
                }

                UpdateRootSizeFromChildren();
            }).ConfigureAwait(false);
        }
        finally
        {
            await MarshalToUiAsync(() =>
            {
                _suspendRefresh = false;
                _loadingRowsTimer.Stop();
            }).ConfigureAwait(false);
        }

        if (!cancellationToken.IsCancellationRequested && IsNodeInCurrentTree(node))
            await MarshalToUiAsync(RebuildRows).ConfigureAwait(false);
    }

    private void OnChildrenBatchLoaded(FolderNode parent)
    {
        if (_rootNode == null || !IsNodeInCurrentTree(parent))
            return;

        var newChildren = parent.Children.Where(c => !c.IsDummy).TakeLast(ChildrenBatchSizeHint);
        foreach (var child in newChildren)
            TrackNode(child);

        if (parent.IsLoadingChildren)
        {
            FolderNodeAnalysis.RefreshPendingAnalysis(parent);
            ScheduleLoadingRowsRefresh();
            return;
        }

        UpdateRootSizeFromChildren();
        UpdateRootSizeText();
        ScheduleLoadingRowsRefresh();
    }

    private const int ChildrenBatchSizeHint = 64;

    private void ScheduleLoadingRowsRefresh()
    {
        _loadingRowsTimer.Stop();
        _loadingRowsTimer.Start();
    }

    private void LoadingRowsTimerOnTick(object? sender, EventArgs e)
    {
        _loadingRowsTimer.Stop();
        RebuildRows();
    }

    private void UpdateRootSizeFromChildren()
    {
        if (_rootNode == null)
            return;

        if (_rootNode.IsLoadingChildren)
            return;

        if (!_rootNode.ChildrenLoaded)
            return;

        var pending = false;
        long knownTotal = 0;
        var total = 0;
        var maxSample = TreeRowBuilder.MaxVisibleChildren;

        foreach (var child in _rootNode.Children)
        {
            if (child.IsDummy)
                continue;

            total++;
            if (total > maxSample)
                continue;

            if (FolderNodeAnalysis.IsSelfPending(child))
                pending = true;
            else if (child.Size >= 0)
                knownTotal += child.Size;
        }

        if (!pending && total <= maxSample && total > 0)
            _rootNode.Size = knownTotal;
        else if (knownTotal > 0)
            _rootNode.Size = knownTotal;

        FolderNodeAnalysis.RefreshPendingAnalysis(_rootNode);
        FolderNodeSorter.UpdateSizeRatios(_rootNode);
    }

    private void ApplySort(FolderNode? node = null)
    {
        var target = node ?? _rootNode;
        if (target == null)
            return;

        FolderNodeSorter.SortRecursive(target, _sortColumn, _sortDirection);
    }

    private void RebuildRows()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RebuildRows);
            return;
        }

        var rows = TreeRowBuilder.Build(_rootNode);
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(row);
    }

    private void ScheduleRefresh()
    {
        if (_suspendRefresh)
            return;

        _refreshPending = true;
        _refreshDebounceTimer.Stop();
        _refreshDebounceTimer.Start();
    }

    private void RefreshDebounceTimerOnTick(object? sender, EventArgs e)
    {
        _refreshDebounceTimer.Stop();

        if (_suspendRefresh || !_refreshPending)
            return;

        _refreshPending = false;
        ApplyPendingRefresh();
    }

    private void ApplyPendingRefresh()
    {
        if (_rootNode == null)
            return;

        UpdateAllSizeRatios(_rootNode);

        if (_sortColumn == SortColumn.Size)
            FolderNodeSorter.SortRecursive(_rootNode, _sortColumn, _sortDirection);

        UpdateRootSizeFromChildren();
        UpdateRootSizeText();
        RebuildRows();
    }

    private static void UpdateAllSizeRatios(FolderNode node)
    {
        if (node.ChildrenLoaded)
            FolderNodeSorter.UpdateSizeRatios(node);

        foreach (var child in node.Children.Where(c => !c.IsDummy && c.IsDirectory))
            UpdateAllSizeRatios(child);
    }

    private void TrackNode(FolderNode node)
    {
        if (node.IsDummy || !_trackedNodes.Add(node))
            return;

        node.PropertyChanged += NodeOnPropertyChanged;
        node.SizeChanged += NodeOnSizeChanged;
        node.IsExpandedChanged += NodeOnIsExpandedChanged;

        foreach (var child in node.Children)
            TrackNode(child);
    }

    private void NodeOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is FolderNode node && e.PropertyName is nameof(FolderNode.IsCalculating)
            or nameof(FolderNode.FormattedSize) or nameof(FolderNode.HasPendingAnalysis)
            or nameof(FolderNode.LoadState) or nameof(FolderNode.ShowsAnalysisProgress))
        {
            if (node == _rootNode || node.Parent == _rootNode)
                MarshalToUi(UpdateRootSizeText);
        }
    }

    private void NodeOnSizeChanged(object? sender, EventArgs e)
    {
        if (_suspendRefresh || sender is not FolderNode node)
            return;

        var ratioParent = node.Parent ?? _rootNode;
        if (ratioParent != null)
            FolderNodeSorter.UpdateSizeRatios(ratioParent);

        if (node.ChildrenLoaded)
            FolderNodeSorter.UpdateSizeRatios(node);

        if (node.Parent == _rootNode || node == _rootNode)
            UpdateRootSizeFromChildren();

        ScheduleRefresh();
    }

    private void NodeOnIsExpandedChanged(object? sender, EventArgs e)
    {
        if (!_suspendRefresh)
            ScheduleRefresh();
    }

    private async void ExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TreeRow row } || row.IsSkeleton || row.IsOverflow || row.Node == null)
            return;
        var node = row.Node;
        if (!node.IsDirectory)
            return;

        try
        {
            if (node.IsExpanded)
            {
                node.IsExpanded = false;
                RebuildRows();
                return;
            }

            node.IsExpanded = true;

            if (!node.ChildrenLoaded)
            {
                RebuildRows();
                await LoadAndPresentAsync(node, GetNavigationToken()).ConfigureAwait(false);
            }
            else
                RebuildRows();
        }
        catch (OperationCanceledException)
        {
            // Ignoré.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Erreur lors de l'ouverture du dossier :\n{ex.Message}",
                "SpaceManager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        var column = tag == "Name" ? SortColumn.Name : SortColumn.Size;

        if (_sortColumn == column)
            _sortDirection = _sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
        else
        {
            _sortColumn = column;
            _sortDirection = column == SortColumn.Size ? SortDirection.Descending : SortDirection.Ascending;
        }

        ApplySort();
        RebuildRows();
        UpdateSortHeaders();
    }

    private void UpdateSortHeaders()
    {
        NameSortHeader.Content = BuildHeaderLabel("Nom", SortColumn.Name);
        SizeSortHeader.Content = BuildHeaderLabel("Taille", SortColumn.Size);
    }

    private string BuildHeaderLabel(string label, SortColumn column)
    {
        if (_sortColumn != column)
            return label;

        var arrow = _sortDirection == SortDirection.Ascending ? " ▲" : " ▼";
        return label + arrow;
    }

    private void UpdateRootSizeText()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateRootSizeText);
            return;
        }

        if (_rootNode == null)
        {
            RootSizeText.Text = "—";
            return;
        }

        RootSizeText.Text = _rootNode.LoadState switch
        {
            SizeLoadState.Queued => "En attente",
            SizeLoadState.Calculating => "Calcul…",
            _ => SizeFormatter.Format(_rootNode.Size)
        };
    }

    private void NavigatePath_Click(object sender, RoutedEventArgs e) => NavigateToPathTextBox();

    private void PathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            NavigateToPathTextBox();
    }

    private void PathTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_rootNode != null)
        {
            var displayed = PathTextBox.Text.Trim().TrimEnd('\\');
            var current = _rootNode.FullPath.TrimEnd('\\');
            if (!string.Equals(displayed, current, StringComparison.OrdinalIgnoreCase))
                PathTextBox.Text = current;
        }
    }

    private void NavigateToPathTextBox()
    {
        if (_isNavigating)
            return;

        var candidate = PathTextBox.Text.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        try
        {
            candidate = PathHelper.NormalizeDirectoryPath(candidate);
        }
        catch
        {
            MessageBox.Show("Chemin invalide.", "SpaceManager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(candidate))
        {
            MessageBox.Show($"Le dossier n'existe pas :\n{candidate}", "SpaceManager", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_rootNode != null && string.Equals(candidate, _rootNode.FullPath, StringComparison.OrdinalIgnoreCase))
            return;

        OpenPath(candidate);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir un dossier à analyser",
            InitialDirectory = _rootNode?.FullPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == true)
            OpenPath(dialog.FolderName);
    }

    private TreeRow? GetContextMenuRow(object sender)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: ListViewItem item } })
            return null;

        return item.DataContext as TreeRow;
    }

    private void ListViewItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListViewItem item || item.DataContext is not TreeRow row || row.IsSkeleton || row.IsOverflow)
        {
            e.Handled = true;
            return;
        }

        if (item.ContextMenu?.Items.Count > 1 && item.ContextMenu.Items[1] is MenuItem openItem && row.Node != null)
        {
            openItem.Header = row.Node.IsDirectory
                ? "Ouvrir le dossier"
                : "Ouvrir le dossier contenant";
        }
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetContextMenuRow(sender);
        if (row == null || row.IsSkeleton || row.Node == null || row.Node.IsDummy)
            return;

        Clipboard.SetText(row.Node.FullPath);
    }

    private void OpenInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetContextMenuRow(sender);
        if (row == null || row.IsSkeleton || row.Node == null || row.Node.IsDummy)
            return;

        try
        {
            if (row.Node.IsDirectory)
                ExplorerHelper.OpenFolder(row.Node.FullPath);
            else
                ExplorerHelper.OpenContainingFolder(row.Node.FullPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible d'ouvrir l'Explorateur Windows :\n{ex.Message}",
                "SpaceManager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshDebounceTimer.Stop();
        _loadingRowsTimer.Stop();
        CancelCurrentNavigation(dispose: true);
        _windowCancellation.Cancel();
        _folderSizeService.CancelAll();
        DetachAllTrackedNodes();
        _rootNode = null;
        _windowCancellation.Dispose();
        base.OnClosed(e);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
