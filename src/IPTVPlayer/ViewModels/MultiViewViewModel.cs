using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Playback;
using LibVLCSharp.Shared;

namespace IPTVPlayer.ViewModels;

/// <summary>
/// Multi-ecrã: até 4 canais ao mesmo tempo, cada um com o seu MediaPlayer (o LibVLC é partilhado).
/// Só o quadro ativo tem som.
/// </summary>
public sealed partial class MultiViewViewModel : ObservableObject, IDisposable
{
    public MultiViewViewModel(LibVlcHost host, PlaylistService playlists, Playlist playlist,
        IReadOnlyList<ChannelItemViewModel> channels, int volume, int networkCachingMs)
    {
        Channels = channels;
        Tiles = Enumerable.Range(1, 4)
            .Select(i => new MultiViewTileViewModel(i, this, host.LibVLC, playlists, playlist, networkCachingMs, volume))
            .ToList();
        SetActive(Tiles[0]);
    }

    /// <summary>O quadro pede um canal: a janela abre o seletor.</summary>
    public event EventHandler<MultiViewTileViewModel>? ChooseRequested;

    public IReadOnlyList<MultiViewTileViewModel> Tiles { get; }

    /// <summary>Canal para o quadro 1 (o que estava a tocar).</summary>
    public ChannelItemViewModel? InitialChannel { get; set; }

    /// <summary>
    /// Só depois de a janela estar desenhada: se o VLC começar antes de o quadro ter a janela
    /// nativa, calcula mal o tamanho do vídeo (aparece ampliado e cortado).
    /// </summary>
    public void Start()
    {
        if (InitialChannel is { } channel) _ = Tiles[0].PlayAsync(channel);
    }

    /// <summary>Canais disponíveis (já sem os grupos bloqueados).</summary>
    public IReadOnlyList<ChannelItemViewModel> Channels { get; }

    [ObservableProperty]
    private MultiViewTileViewModel? _activeTile;

    public void SetActive(MultiViewTileViewModel tile)
    {
        ActiveTile = tile;
        foreach (var t in Tiles) t.IsActive = ReferenceEquals(t, tile);
    }

    internal void RequestChoose(MultiViewTileViewModel tile) => ChooseRequested?.Invoke(this, tile);

    public void Dispose()
    {
        foreach (var t in Tiles) t.Dispose();
    }
}

public sealed partial class MultiViewTileViewModel : ObservableObject, IDisposable
{
    private readonly MultiViewViewModel _owner;
    private readonly LibVLC _libVlc;
    private readonly PlaylistService _playlists;
    private readonly Playlist _playlist;
    private readonly int _networkCachingMs;
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private Media? _media;
    private int _generation;

    public MultiViewTileViewModel(int number, MultiViewViewModel owner, LibVLC libVlc, PlaylistService playlists,
        Playlist playlist, int networkCachingMs, int volume)
    {
        Number = number;
        _owner = owner;
        _libVlc = libVlc;
        _playlists = playlists;
        _playlist = playlist;
        _networkCachingMs = networkCachingMs;
        MediaPlayer = new MediaPlayer(libVlc) { Mute = true };
        MediaPlayer.Playing += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            Status = null;
            // O VLC repõe o volume quando abre o áudio.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                MediaPlayer.Volume = volume;
                MediaPlayer.Mute = !_isActiveForAudio;
            });
        });
        MediaPlayer.EncounteredError += (_, _) => _dispatcher.BeginInvoke(() => Status = "Sem sinal (offline ou limite de ligações)");
        MediaPlayer.EndReached += (_, _) => _dispatcher.BeginInvoke(() => Status = "A transmissão terminou");
    }

    public int Number { get; }
    public MediaPlayer MediaPlayer { get; }
    public string NumberText => Number.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private ChannelItemViewModel? _channel;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isActive;

    private volatile bool _isActiveForAudio;

    public bool IsEmpty => Channel is null;

    partial void OnIsActiveChanged(bool value)
    {
        _isActiveForAudio = value;
        ThreadPool.QueueUserWorkItem(_ => MediaPlayer.Mute = !value);
    }

    [RelayCommand]
    private void Choose() => _owner.RequestChoose(this);

    [RelayCommand]
    private void Activate() => _owner.SetActive(this);

    [RelayCommand]
    private async Task ClearAsync()
    {
        _generation++;
        Channel = null;
        Status = null;
        await Task.Run(() =>
        {
            MediaPlayer.Stop();
            _media?.Dispose();
            _media = null;
        });
    }

    public async Task PlayAsync(ChannelItemViewModel channel)
    {
        var generation = ++_generation;
        Channel = channel;
        Status = "A abrir…";
        try
        {
            var url = await Task.Run(() => _playlists.ResolveStreamUrl(_playlist, channel.Model));
            if (generation != _generation) return;
            await Task.Run(() =>
            {
                var options = new List<string> { $":network-caching={_networkCachingMs}" };
                if (!string.IsNullOrWhiteSpace(channel.Model.UserAgent)) options.Add($":http-user-agent={channel.Model.UserAgent}");
                if (!string.IsNullOrWhiteSpace(channel.Model.Referrer)) options.Add($":http-referrer={channel.Model.Referrer}");
                var media = new Media(_libVlc, new Uri(url), options.ToArray());
                var previous = _media;
                _media = media;
                MediaPlayer.Play(media);
                previous?.Dispose();
            });
        }
        catch (UserFacingException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("Erro no multi-ecrã.", ex);
            Status = "Não foi possível abrir este canal.";
        }
    }

    public void Dispose()
    {
        _generation++;
        var player = MediaPlayer;
        var media = _media;
        // Stop pode bloquear num stream parado: fora da UI.
        Task.Run(() =>
        {
            try
            {
                player.Stop();
                player.Dispose();
                media?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Erro ao fechar quadro do multi-ecrã.", ex);
            }
        });
    }
}
