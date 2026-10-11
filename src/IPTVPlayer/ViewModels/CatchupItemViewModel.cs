using IPTVPlayer.Services.Epg;

namespace IPTVPlayer.ViewModels;

/// <summary>Programa em catch-up (já passou, ou "ver desde o início" do que está a dar).</summary>
public sealed class CatchupItemViewModel : PlayableItemViewModel
{
    public CatchupItemViewModel(ChannelItemViewModel channel, EpgProgramme programme)
    {
        Channel = channel;
        Programme = programme;
    }

    public ChannelItemViewModel Channel { get; }
    public EpgProgramme Programme { get; }
    public override string Name => Programme.Title;
    public override string Subtitle => $"{Channel.Name} · {Programme.StartLocal:dd/MM HH:mm} (arquivo)";
    public override string NumberText => Channel.NumberText;
    public override bool IsLive => false;
}
