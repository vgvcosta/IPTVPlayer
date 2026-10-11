using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Net;
using IPTVPlayer.Services.UI;

namespace IPTVPlayer.ViewModels;

/// <summary>Estado das atualizações, partilhado pela barra de estado e pelas definições.</summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly IDialogService _dialogs;
    private UpdateInfo? _available;

    public UpdateViewModel(UpdateService updates, IDialogService dialogs)
    {
        _updates = updates;
        _dialogs = dialogs;
    }

    public string CurrentVersionText => $"Versão instalada: {UpdateService.CurrentVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _statusIsError;

    /// <summary>0–100 durante o download; -1 fora dele.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    private double _downloadPercent = -1;

    public bool IsDownloading => DownloadPercent >= 0;

    public string InstallButtonText => _available is { } u ? $"Atualizar para {u.Version}" : "Atualizar";

    /// <summary>Verificação silenciosa no arranque: só mostra algo se houver versão nova.</summary>
    public async Task CheckSilentlyAsync()
    {
        try
        {
            await CheckCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"Verificação de atualizações falhou: {ex.Message}");
            Status = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        SetStatus("A procurar atualizações…", false);
        try
        {
            await CheckCoreAsync();
            if (!IsUpdateAvailable) SetStatus("Tens a versão mais recente.", false);
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao procurar atualizações.", ex);
            SetStatus(ex is UserFacingException ? ex.Message : "Não foi possível procurar atualizações.", true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAsync()
    {
        if (_available is not { } update) return;

        if (!UpdateService.IsInstalled)
        {
            // Versão portátil (.zip): não há instalador para substituir; abre a página da release.
            UpdateService.OpenReleasesPage(update.PageUrl);
            return;
        }

        if (!_dialogs.Confirm("Atualizar o PepinoTv",
                $"Vai ser instalada a versão {update.Version}.\n\nA app fecha durante a instalação e volta a abrir no fim. " +
                "As tuas playlists, favoritos e definições ficam como estão.\n\nContinuar?"))
            return;

        IsBusy = true;
        DownloadPercent = 0;
        SetStatus($"A transferir a versão {update.Version}…", false);
        try
        {
            var progress = new Progress<double>(p => DownloadPercent = Math.Round(p * 100));
            var msi = await _updates.DownloadAsync(update, progress);
            SetStatus("A instalar…", false);
            UpdateService.LaunchInstaller(msi);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warn("Erro ao instalar a atualização.", ex);
            SetStatus(ex is UserFacingException ? ex.Message : "Não foi possível transferir a atualização.", true);
            IsBusy = false;
            DownloadPercent = -1;
        }
    }

    [RelayCommand]
    private void OpenReleases() => UpdateService.OpenReleasesPage(_available?.PageUrl);

    private bool CanRun() => !IsBusy;

    private async Task CheckCoreAsync()
    {
        _available = await _updates.CheckAsync();
        IsUpdateAvailable = _available is not null;
        OnPropertyChanged(nameof(InstallButtonText));
        if (_available is { } u)
            SetStatus($"Há uma versão nova: {u.Version}.", false);
    }

    private void SetStatus(string text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }
}
