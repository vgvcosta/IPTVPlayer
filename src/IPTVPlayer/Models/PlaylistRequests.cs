namespace IPTVPlayer.Models;

/// <summary>Dados introduzidos pelo utilizador para criar uma playlist.</summary>
public sealed record NewPlaylistRequest(
    PlaylistKind Kind,
    string? Name,
    string Location,
    string? Username = null,
    string? Password = null,
    string? CustomEpgUrl = null);

/// <summary>Resultado de editar uma playlist. NewChannels só vem preenchido se a origem mudou.</summary>
public sealed record PlaylistEditResult(Playlist Playlist, IReadOnlyList<Channel>? NewChannels, bool EpgSourceChanged);

/// <summary>Dados atuais (desencriptados) para preencher o diálogo de edição. A password nunca é devolvida.</summary>
public sealed record PlaylistEditInfo(PlaylistKind Kind, string Name, string Location, string? Username, string? CustomEpgUrl);

/// <summary>Playlist com os respetivos canais já carregados.</summary>
public sealed record PlaylistData(Playlist Playlist, IReadOnlyList<Channel> Channels);

/// <summary>Progresso de uma operação longa. Fraction nulo = indeterminado.</summary>
public sealed record LoadProgress(string Message, double? Fraction = null);
