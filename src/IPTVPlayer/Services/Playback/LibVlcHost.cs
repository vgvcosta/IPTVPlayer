using LibVLCSharp.Shared;

namespace IPTVPlayer.Services.Playback;

/// <summary>Uma única instância do LibVLC para toda a app (player, gravações e multi-ecrã).</summary>
public sealed class LibVlcHost : IDisposable
{
    public LibVlcHost()
    {
        LibVLC = new LibVLC(enableDebugLogs: false, "--no-video-title-show", "--no-snapshot-preview");
    }

    public LibVLC LibVLC { get; }

    public void Dispose()
    {
        try
        {
            LibVLC.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao libertar o LibVLC.", ex);
        }
    }
}
