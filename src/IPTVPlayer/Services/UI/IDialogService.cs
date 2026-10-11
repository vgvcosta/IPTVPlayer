using IPTVPlayer.Models;
using IPTVPlayer.ViewModels;

namespace IPTVPlayer.Services.UI;

public interface IDialogService
{
    /// <summary>Mostra o diálogo "Adicionar playlist". Devolve null se o utilizador cancelar.</summary>
    PlaylistData? ShowAddPlaylist();

    /// <summary>Editar playlist. Devolve null se o utilizador cancelar.</summary>
    PlaylistEditResult? ShowEditPlaylist(Playlist playlist);

    /// <summary>Guia TV. Devolve o canal a ver, ou null.</summary>
    GuideResult? ShowGuide(GuideViewModel guide);

    /// <summary>Pede o PIN até <paramref name="validate"/> o aceitar ou o utilizador cancelar.</summary>
    bool AskPin(string title, string message, Func<string, bool> validate);

    void ShowSettings(Playlist? currentPlaylist);

    void ShowMultiView(MultiViewViewModel multiView);

    /// <summary>Lista de gravações. Devolve a gravação a ver, ou null.</summary>
    IPTVPlayer.Services.Data.Recording? ShowRecordings();

    bool Confirm(string title, string message);

    string? PickPlaylistFile();
}
