using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Data;
using IPTVPlayer.Services.Playback;
using IPTVPlayer.Services.UI;

namespace IPTVPlayer.ViewModels;

public sealed class RecordingItemViewModel
{
    public RecordingItemViewModel(Recording model, long sizeBytes)
    {
        Model = model;
        SizeText = sizeBytes <= 0 ? "" : sizeBytes >= 1024L * 1024 * 1024
            ? $"{sizeBytes / 1024d / 1024 / 1024:0.0} GB"
            : $"{sizeBytes / 1024d / 1024:0} MB";
    }

    public Recording Model { get; }
    public string Title => Model.Title;
    public string ChannelName => Model.ChannelName;
    public string TimeText => $"{Model.StartUtc.ToLocalTime():dd/MM HH:mm} – {Model.StopUtc.ToLocalTime():HH:mm}";
    public string SizeText { get; }
    public string? Error => Model.Error;
    public bool IsActive => Model.Status == RecordingStatus.Recording;
    public bool IsScheduled => Model.Status == RecordingStatus.Scheduled;
    public bool CanStop => IsActive || IsScheduled;
    public bool CanPlay => Model.Status is RecordingStatus.Completed or RecordingStatus.Recording && File.Exists(Model.FilePath);

    public string StatusText => Model.Status switch
    {
        RecordingStatus.Scheduled => "Agendada",
        RecordingStatus.Recording => "● A gravar",
        RecordingStatus.Completed => "Concluída",
        RecordingStatus.Failed => "Falhou",
        RecordingStatus.Cancelled => "Cancelada",
        _ => "",
    };

    public override string ToString() => $"{Title} ({StatusText})";
}

/// <summary>Lista de gravações: ver, parar/cancelar, apagar e abrir a pasta.</summary>
public sealed partial class RecordingsViewModel : ObservableObject
{
    private readonly RecordingService _recordings;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _refresh;

    public RecordingsViewModel(RecordingService recordings, IDialogService dialogs)
    {
        _recordings = recordings;
        _dialogs = dialogs;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refresh.Tick += async (_, _) => await LoadAsync();
        _recordings.Changed += OnChanged;
    }

    /// <summary>Gravação a ver (a janela fecha e o player principal abre-a).</summary>
    public event EventHandler<Recording>? PlayRequested;

    [ObservableProperty]
    private IReadOnlyList<RecordingItemViewModel> _items = [];

    [ObservableProperty]
    private string? _message;

    public string Folder => _recordings.Folder;

    public async Task LoadAsync()
    {
        var items = await Task.Run(() => _recordings.GetAll()
            .Select(r => new RecordingItemViewModel(r, RecordingService.TotalSize(r.FilePath))).ToList());
        Items = items;
    }

    public void Activate() => _refresh.Start();

    public void Deactivate()
    {
        _refresh.Stop();
        _recordings.Changed -= OnChanged;
    }

    [RelayCommand]
    private void Play(RecordingItemViewModel? item)
    {
        if (item is { CanPlay: true }) PlayRequested?.Invoke(this, item.Model);
    }

    [RelayCommand]
    private async Task StopAsync(RecordingItemViewModel? item)
    {
        if (item is null) return;
        await Task.Run(() => _recordings.Stop(item.Model.Id));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(RecordingItemViewModel? item)
    {
        if (item is null) return;
        var hasFile = RecordingService.PartFiles(item.Model.FilePath).Any();
        var question = hasFile
            ? $"Apagar a gravação \"{item.Title}\"?\nO ficheiro de vídeo também é apagado do disco."
            : $"Remover \"{item.Title}\" da lista?";
        if (!_dialogs.Confirm("Apagar gravação", question)) return;

        try
        {
            await Task.Run(() => _recordings.Delete(item.Model.Id, deleteFiles: true));
            Message = null;
        }
        catch (UserFacingException ex)
        {
            Message = ex.Message;
        }
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenFolder(RecordingItemViewModel? item)
    {
        try
        {
            if (item?.Model.FilePath is { } path && File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            }
            Directory.CreateDirectory(Folder);
            Process.Start("explorer.exe", $"\"{Folder}\"");
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível abrir a pasta das gravações.", ex);
            Message = "Não foi possível abrir a pasta.";
        }
    }

    private void OnChanged(object? sender, EventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () => await LoadAsync());
}
