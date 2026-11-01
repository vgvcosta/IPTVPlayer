using LibVLCSharp.Shared;

namespace IPTVPlayer.Services.Playback;

/// <summary>Uma única instância do LibVLC para toda a app (player, gravações e multi-ecrã).</summary>
public sealed class LibVlcHost : IDisposable
{
    public LibVlcHost()
    {
        // clock-jitter/clock-synchro: em diretos com tempos irregulares, o VLC deitava fora todo o áudio por "atraso".
        LibVLC = new LibVLC(enableDebugLogs: false, "--no-video-title-show", "--no-snapshot-preview",
            "--clock-jitter=0", "--clock-synchro=0");
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
