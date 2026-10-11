using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Models;
using IPTVPlayer.Services;
using IPTVPlayer.Services.UI;

namespace IPTVPlayer.ViewModels;

public sealed partial class AddPlaylistViewModel : ObservableObject
{
    private readonly PlaylistService _playlists;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    public AddPlaylistViewModel(PlaylistService playlists, IDialogService dialogs)
    {
        _playlists = playlists;
        _dialogs = dialogs;
    }

    /// <summary>Pedido de fecho do diálogo: true = playlist adicionada.</summary>
    public event EventHandler<bool>? RequestClose;

    public PlaylistData? Result { get; private set; }

    /// <summary>Resultado em modo de edição.</summary>
    public PlaylistEditResult? EditResult { get; private set; }

    private Playlist? _editing;

    public bool IsEditMode => _editing is not null;
    public string Title => IsEditMode ? "Editar playlist" : "Adicionar playlist";
    public string SubmitText => IsEditMode ? "Guardar" : "Adicionar";
    public string PasswordHint => IsEditMode ? "Deixa vazio para manter a password atual." : "";

    [ObservableProperty]
    private string _customEpgUrl = "";

    /// <summary>Prepara o diálogo para editar uma playlist existente (o tipo não pode mudar).</summary>
    public void BeginEdit(Playlist playlist)
    {
        var info = _playlists.GetEditInfo(playlist);
        _editing = playlist;
        Kind = info.Kind;
        Name = info.Name;
        CustomEpgUrl = info.CustomEpgUrl ?? "";
        switch (info.Kind)
        {
            case PlaylistKind.M3uUrl: Url = info.Location; break;
            case PlaylistKind.M3uFile: FilePath = info.Location; break;
            default:
                Server = info.Location;
                Username = info.Username ?? "";
                break;
        }
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(PasswordHint));
    }

    /// <summary>Definido pela view (o PasswordBox não suporta binding, de propósito).</summary>
    public string Password { private get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsM3uUrl), nameof(IsM3uFile), nameof(IsXtream))]
    private PlaylistKind _kind = PlaylistKind.M3uUrl;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _url = "";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _server = "";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _progressMessage;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsM3uUrl
    {
        get => Kind == PlaylistKind.M3uUrl;
        set { if (value) Kind = PlaylistKind.M3uUrl; }
    }

    public bool IsM3uFile
    {
        get => Kind == PlaylistKind.M3uFile;
        set { if (value) Kind = PlaylistKind.M3uFile; }
    }

    public bool IsXtream
    {
        get => Kind == PlaylistKind.Xtream;
        set { if (value) Kind = PlaylistKind.Xtream; }
    }

    partial void OnKindChanged(PlaylistKind value) => ErrorMessage = null;

    [RelayCommand]
    private void Browse()
    {
        var path = _dialogs.PickPlaylistFile();
        if (path is null) return;
        FilePath = path;
        if (string.IsNullOrWhiteSpace(Name)) Name = Path.GetFileNameWithoutExtension(path);
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        ErrorMessage = null;
        var request = Kind switch
        {
            PlaylistKind.M3uUrl => new NewPlaylistRequest(Kind, Name, Url, CustomEpgUrl: CustomEpgUrl),
            PlaylistKind.M3uFile => new NewPlaylistRequest(Kind, Name, FilePath, CustomEpgUrl: CustomEpgUrl),
            _ => new NewPlaylistRequest(Kind, Name, Server, Username, Password, CustomEpgUrl),
        };

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressMessage = "A preparar…";
        IsProgressIndeterminate = true;
        var progress = new Progress<LoadProgress>(p =>
        {
            ProgressMessage = p.Message;
            IsProgressIndeterminate = p.Fraction is null;
            Progress = p.Fraction ?? 0;
        });

        try
        {
            if (_editing is not null)
                EditResult = await _playlists.UpdateAsync(_editing, request, progress, _cts.Token);
            else
                Result = await _playlists.AddAsync(request, progress, _cts.Token);
            IsBusy = false;
            RequestClose?.Invoke(this, true);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Operação cancelada.";
        }
        catch (UserFacingException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Error("Erro inesperado ao guardar a playlist.", ex);
            ErrorMessage = "Ocorreu um erro inesperado ao guardar a playlist. Consulta o ficheiro de log para mais detalhes.";
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
            _cts?.Cancel();
        else
            RequestClose?.Invoke(this, false);
    }
}
