using CommunityToolkit.Mvvm.ComponentModel;
using IPTVPlayer.Models;

namespace IPTVPlayer.ViewModels;

public sealed partial class PlaylistItemViewModel : ObservableObject
{
    public PlaylistItemViewModel(Playlist model)
    {
        Model = model;
        _channelCount = model.ChannelCount;
    }

    public Playlist Model { get; }
    public string Name => Model.Name;
    public string KindLabel => Model.KindLabel;

    [ObservableProperty]
    private int _channelCount;

    /// <summary>Depois de editar a playlist.</summary>
    public void Refresh()
    {
        ChannelCount = Model.ChannelCount;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(KindLabel));
    }

    /// <summary>Usado por leitores de ecrã / UI Automation.</summary>
    public override string ToString() => Name;
}
