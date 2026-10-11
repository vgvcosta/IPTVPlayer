using CommunityToolkit.Mvvm.ComponentModel;

namespace IPTVPlayer.ViewModels;

/// <summary>Qualquer coisa que o player pode mostrar: canal em direto, filme ou episódio.</summary>
public abstract partial class PlayableItemViewModel : ObservableObject
{
    /// <summary>É o item que está a tocar (destacado nas listas).</summary>
    [ObservableProperty]
    private bool _isPlaying;

    public abstract string Name { get; }

    /// <summary>Segunda linha do OSD (grupo, programa atual, série/temporada…).</summary>
    public abstract string? Subtitle { get; }

    /// <summary>Número mostrado no OSD (só canais).</summary>
    public virtual string? NumberText => null;

    public abstract bool IsLive { get; }

    /// <summary>Usado por leitores de ecrã / UI Automation.</summary>
    public override string ToString() => Name;
}
