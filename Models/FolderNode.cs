using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SpaceManager.Services;

namespace SpaceManager.Models;

public sealed class FolderNode : INotifyPropertyChanged
{
    private long _size = -1;
    private double _sizeRatio;
    private bool _isCalculating;
    private bool _isQueued;
    private bool _hasDummyChild = true;
    private bool _isExpanded;
    private bool _childrenLoaded;
    private bool _isLoadingChildren;
    private bool _hasPendingAnalysis;

    public FolderNode(string fullPath, string name, bool isDirectory, FolderNode? parent = null)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
        Parent = parent;
        Children = new ObservableCollection<FolderNode>();

        if (isDirectory)
            Children.Add(CreateDummyNode());
    }

    public event EventHandler? SizeChanged;
    public event EventHandler? IsExpandedChanged;

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; }

    public bool HasDummyChild
    {
        get => _hasDummyChild;
        set => SetField(ref _hasDummyChild, value);
    }

    public bool ChildrenLoaded
    {
        get => _childrenLoaded;
        set
        {
            if (SetField(ref _childrenLoaded, value))
                RefreshAnalysisState();
        }
    }

    public bool IsLoadingChildren
    {
        get => _isLoadingChildren;
        set
        {
            if (SetField(ref _isLoadingChildren, value))
                RefreshAnalysisState();
        }
    }

    public bool HasPendingAnalysis => _hasPendingAnalysis;

    internal void SetHasPendingAnalysis(bool value)
    {
        if (SetField(ref _hasPendingAnalysis, value))
            NotifySizeStateChanged();
    }

    private void RefreshAnalysisState() => FolderNodeAnalysis.RefreshPendingAnalysis(this);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
                IsExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsCalculating
    {
        get => _isCalculating;
        set
        {
            if (SetField(ref _isCalculating, value))
            {
                NotifySizeStateChanged();
                RefreshAnalysisState();
            }
        }
    }

    public bool IsQueued
    {
        get => _isQueued;
        set
        {
            if (SetField(ref _isQueued, value))
            {
                NotifySizeStateChanged();
                RefreshAnalysisState();
            }
        }
    }

    public SizeLoadState LoadState
    {
        get
        {
            if (FolderNodeAnalysis.ShowsProgress(this))
                return SizeLoadState.Calculating;

            if (IsQueued)
                return SizeLoadState.Queued;

            return SizeLoadState.Ready;
        }
    }

    public bool IsSizeReady => LoadState == SizeLoadState.Ready && Size >= 0;

    public bool ShowsAnalysisProgress => LoadState == SizeLoadState.Calculating;

    internal void NotifyChildAnalysisChanged() => NotifySizeStateChanged();

    public long Size
    {
        get => _size;
        set
        {
        if (SetField(ref _size, value))
        {
            NotifySizeStateChanged();
            SizeChanged?.Invoke(this, EventArgs.Empty);
            RefreshAnalysisState();
        }
        }
    }

    public double SizeRatio
    {
        get => _sizeRatio;
        set
        {
            if (SetField(ref _sizeRatio, value))
                OnPropertyChanged(nameof(BarWidth));
        }
    }

    public double BarWidth => SizeRatio * 180;

    public string FormattedSize => LoadState switch
    {
        SizeLoadState.Queued => "En attente",
        SizeLoadState.Calculating => "Calcul…",
        _ when Size < 0 => "—",
        _ => SizeFormatter.Format(Size)
    };

    public static FolderNode CreateDummyNode() =>
        new(string.Empty, string.Empty, isDirectory: false) { HasDummyChild = false };

    public bool IsDummy => string.IsNullOrEmpty(FullPath);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void NotifySizeStateChanged()
    {
        OnPropertyChanged(nameof(FormattedSize));
        OnPropertyChanged(nameof(BarWidth));
        OnPropertyChanged(nameof(LoadState));
        OnPropertyChanged(nameof(IsSizeReady));
        OnPropertyChanged(nameof(ShowsAnalysisProgress));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
