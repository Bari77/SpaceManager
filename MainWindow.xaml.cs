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
    private readonly HashSet<FolderNode> _trackedNodes = [];
    private readonly FolderSizeService _folderSizeService;
    private readonly DispatcherTimer _refreshDebounceTimer;
    private FolderNode? _rootNode;
    private SortColumn _sortColumn = SortColumn.Size;
    private SortDirection _sortDirection = SortDirection.Descending;
    private bool _isNavigating;
    private bool _suspendRefresh;
    private bool _refreshPending;

    public MainWindow(string initialPath)
    {
        _folderSizeService = new FolderSizeService(MarshalToUi);
        _refreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshDebounceTimer.Tick += RefreshDebounceTimerOnTick;

        InitializeComponent();
        SetApplicationIcon();
        ConfigureRowContextMenu();
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

    private void MarshalToUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.BeginInvoke(action);
    }

    public void OpenPath(string path)
    {
        Activate();
        Focus();
        LoadPath(path);
    }

    private void LoadPath(string path)
    {
        _folderSizeService.CancelAll();
        _refreshDebounceTimer.Stop();

        if (_rootNode != null)
            UntrackNode(_rootNode);

        Rows.Clear();

        var fullPath = PathHelper.NormalizeDirectoryPath(path);
        _isNavigating = true;
        PathTextBox.Text = fullPath.TrimEnd('\\');
        _isNavigating = false;

        _rootNode = new FolderNode(fullPath, PathHelper.GetDisplayName(fullPath), isDirectory: true);
        _rootNode.SizeRatio = 1;
        TrackNode(_rootNode);

        _ = ExpandRootAsync(_rootNode);
    }

    private async Task ExpandRootAsync(FolderNode rootNode)
    {
        try
        {
            rootNode.IsExpanded = true;
            await LoadAndPresentAsync(rootNode).ConfigureAwait(true);
            UpdateRootSizeFromChildren();
            UpdateRootSizeText();
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

    private async Task LoadAndPresentAsync(FolderNode node)
    {
        _suspendRefresh = true;
        try
        {
            await _folderSizeService.LoadChildrenAsync(node, _windowCancellation.Token).ConfigureAwait(true);

            foreach (var child in node.Children.Where(c => !c.IsDummy))
                TrackNode(child);

            ApplySort(node);
            FolderNodeSorter.UpdateSizeRatios(node);
            UpdateRootSizeFromChildren();
        }
        finally
        {
            _suspendRefresh = false;
        }

        RebuildRows();
    }

    private void UpdateRootSizeFromChildren()
    {
        if (_rootNode == null || !_rootNode.ChildrenLoaded)
            return;

        var children = _rootNode.Children.Where(c => !c.IsDummy).ToList();
        if (children.Count == 0)
            return;

        var pending = children.Any(c => c.Size < 0 || c.IsCalculating || c.IsQueued);
        var knownTotal = children.Where(c => c.Size >= 0).Sum(c => c.Size);

        _rootNode.IsCalculating = pending;
        if (!pending)
            _rootNode.Size = knownTotal;
        else if (knownTotal > 0)
            _rootNode.Size = knownTotal;
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

    private void UntrackNode(FolderNode node)
    {
        if (!_trackedNodes.Remove(node))
            return;

        node.PropertyChanged -= NodeOnPropertyChanged;
        node.SizeChanged -= NodeOnSizeChanged;
        node.IsExpandedChanged -= NodeOnIsExpandedChanged;

        foreach (var child in node.Children.ToList())
            UntrackNode(child);
    }

    private void UntrackAllNodes()
    {
        foreach (var node in _trackedNodes.ToList())
            UntrackNode(node);

        _trackedNodes.Clear();
        _rootNode = null;
    }

    private void NodeOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is FolderNode node && e.PropertyName is nameof(FolderNode.IsCalculating) or nameof(FolderNode.FormattedSize))
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
        if (sender is not Button { Tag: TreeRow row })
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
                await LoadAndPresentAsync(node).ConfigureAwait(true);
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

        RootSizeText.Text = _rootNode.IsCalculating || _rootNode.Size < 0
            ? "Calcul…"
            : SizeFormatter.Format(_rootNode.Size);
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
        if (sender is not ListViewItem item || item.DataContext is not TreeRow row)
            return;

        if (item.ContextMenu?.Items.Count > 1 && item.ContextMenu.Items[1] is MenuItem openItem)
        {
            openItem.Header = row.Node.IsDirectory
                ? "Ouvrir le dossier"
                : "Ouvrir le dossier contenant";
        }
    }

    private void CopyPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetContextMenuRow(sender);
        if (row == null || row.Node.IsDummy)
            return;

        Clipboard.SetText(row.Node.FullPath);
    }

    private void OpenInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = GetContextMenuRow(sender);
        if (row == null || row.Node.IsDummy)
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
        _windowCancellation.Cancel();
        _folderSizeService.CancelAll();
        UntrackAllNodes();
        base.OnClosed(e);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
