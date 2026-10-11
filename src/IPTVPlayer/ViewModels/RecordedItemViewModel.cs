using IPTVPlayer.Services.Data;

namespace IPTVPlayer.ViewModels;

/// <summary>Gravação a ser vista no player.</summary>
public sealed class RecordedItemViewModel : PlayableItemViewModel
{
    public RecordedItemViewModel(Recording recording)
    {
        Recording = recording;
    }

    public Recording Recording { get; }
    public override string Name => Recording.Title;
    public override string Subtitle => $"{Recording.ChannelName} · gravado em {Recording.StartUtc.ToLocalTime():dd/MM HH:mm}";
    public override bool IsLive => false;
}