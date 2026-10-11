using CommunityToolkit.Mvvm.ComponentModel;

namespace IPTVPlayer.ViewModels;

public enum GroupKind
{
    All,
    Favorites,
    ContinueWatching,
    Group,
}

public sealed partial class GroupItemViewModel : ObservableObject
{
    public GroupItemViewModel(GroupKind kind, string name, int count)
    {
        Kind = kind;
        Name = name;
        _count = count;
    }

    public GroupKind Kind { get; }
    public string Name { get; }

    [ObservableProperty]
    private int _count;

    /// <summary>Bloqueado pelo controlo parental.</summary>
    [ObservableProperty]
    private bool _isLocked;

    /// <summary>Usado por leitores de ecrã / UI Automation.</summary>
    public override string ToString() => Name;
}
