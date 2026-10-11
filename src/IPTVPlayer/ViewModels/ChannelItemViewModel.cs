using CommunityToolkit.Mvvm.ComponentModel;
using IPTVPlayer.Models;
using IPTVPlayer.Services;

namespace IPTVPlayer.ViewModels;

public sealed partial class ChannelItemViewModel : PlayableItemViewModel
{
    public ChannelItemViewModel(Channel model, int number)
    {
        Model = model;
        Number = number;
        SearchKey = TextNormalizer.ForSearch(model.Name);
        Initials = TextNormalizer.Initials(model.Name);
    }

    public Channel Model { get; }
    public int Number { get; }
    public string Key => Model.Key;
    public override string Name => Model.Name;
    public string GroupTitle => Model.GroupTitle;
    public string? LogoUrl => Model.LogoUrl;
    public override bool IsLive => true;
    public override string NumberText => Number.ToString();
    public override string Subtitle => NowTitle is null ? GroupTitle : $"Agora: {NowTitle}";

    /// <summary>Nome normalizado (minúsculas, sem acentos) para pesquisa rápida.</summary>
    public string SearchKey { get; }
    public string Initials { get; }

    /// <summary>Id do canal no XMLTV (null = sem EPG).</summary>
    public string? EpgId { get; set; }

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(HasEpg))]
    private string? _nowTitle;

    [ObservableProperty]
    private double _nowProgress;

    [ObservableProperty]
    private string? _epgToolTip;

    public bool HasEpg => NowTitle is not null;
}
