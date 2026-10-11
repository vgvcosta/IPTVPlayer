using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IPTVPlayer.Services;
using IPTVPlayer.Services.Epg;

namespace IPTVPlayer.ViewModels;

/// <summary>
/// Grelha de programação: uma linha por canal e uma janela de tempo (3 h) que se desloca com ◀ / ▶.
/// Os blocos de cada linha só são calculados quando a linha fica visível (a lista é virtualizada).
/// </summary>
public sealed partial class GuideViewModel : ObservableObject
{
    public const int WindowHours = 3;

    private readonly EpgGuide _guide;
    private readonly IReadOnlyList<ChannelItemViewModel> _channels;
    private readonly EpgService _epgService;
    private readonly long _playlistId;
    private readonly List<GuideRowViewModel> _allRows;
    private int _descriptionRequest;

    public GuideViewModel(EpgGuide guide, IReadOnlyList<ChannelItemViewModel> channels, EpgService epgService,
        long playlistId, string groupName)
    {
        _guide = guide;
        _channels = channels;
        _epgService = epgService;
        _playlistId = playlistId;
        GroupName = groupName;
        _allRows = channels.Select(c => new GuideRowViewModel(c, this)).ToList();
        _windowStart = RoundDown(DateTime.Now);
        ApplyFilter();
        UpdateWindow();
    }

    public event EventHandler<GuideResult?>? RequestClose;

    public string GroupName { get; }

    /// <summary>Muda sempre que a janela de tempo muda (invalida os blocos já calculados).</summary>
    public int Version { get; private set; }

    [ObservableProperty]
    private IReadOnlyList<GuideRowViewModel> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowEnd), nameof(DateLabel))]
    private DateTime _windowStart;

    [ObservableProperty]
    private IReadOnlyList<TimeMarker> _markers = [];

    [ObservableProperty]
    private double _nowFraction = -1;

    [ObservableProperty]
    private bool _onlyWithEpg = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanWatchCatchup), nameof(CatchupButtonText), nameof(CanRecord))]
    [NotifyCanExecuteChangedFor(nameof(WatchSelectedCommand), nameof(WatchCatchupCommand), nameof(RecordCommand))]
    private ProgrammeBlockViewModel? _selected;

    [ObservableProperty]
    private string? _selectedDescription;

    public DateTime WindowEnd => WindowStart.AddHours(WindowHours);

    public string DateLabel => WindowStart.Date == DateTime.Today ? "Hoje"
        : WindowStart.Date == DateTime.Today.AddDays(1) ? "Amanhã"
        : WindowStart.Date == DateTime.Today.AddDays(-1) ? "Ontem"
        : WindowStart.ToString("dddd, d 'de' MMMM", PtCulture);

    public bool HasSelection => Selected is not null;

    internal long WindowStartUnix => new DateTimeOffset(WindowStart).ToUnixTimeSeconds();
    internal long WindowEndUnix => new DateTimeOffset(WindowEnd).ToUnixTimeSeconds();

    private static readonly System.Globalization.CultureInfo PtCulture = new("pt-PT");

    internal IReadOnlyList<EpgProgramme> GetProgrammes(string epgId) => _guide.GetRange(epgId, WindowStartUnix, WindowEndUnix);

    [RelayCommand]
    private void Previous() => WindowStart = WindowStart.AddHours(-1);

    [RelayCommand]
    private void Next() => WindowStart = WindowStart.AddHours(1);

    [RelayCommand]
    private void GoToNow() => WindowStart = RoundDown(DateTime.Now);

    [RelayCommand]
    private void Select(ProgrammeBlockViewModel? block) => Selected = block;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void WatchSelected()
    {
        if (Selected is { } s) RequestClose?.Invoke(this, new GuideResult(s.Row.Channel, null));
    }

    /// <summary>Ver do arquivo (catch-up) o programa escolhido: um que já passou, ou o atual desde o início.</summary>
    [RelayCommand(CanExecute = nameof(CanWatchCatchup))]
    private void WatchCatchup()
    {
        if (Selected is { CanCatchup: true } s) RequestClose?.Invoke(this, new GuideResult(s.Row.Channel, s.Programme));
    }

    public bool CanWatchCatchup => Selected?.CanCatchup == true;

    /// <summary>Agendar gravação (devolve a mensagem a mostrar). Definido pela janela principal.</summary>
    public Func<ChannelItemViewModel, EpgProgramme, string>? ScheduleRecording { get; init; }

    [ObservableProperty]
    private string? _recordMessage;

    public bool CanRecord => Selected is { IsPast: false } && ScheduleRecording is not null;

    [RelayCommand(CanExecute = nameof(CanRecord))]
    private void Record()
    {
        if (Selected is { } s && ScheduleRecording is { } schedule) RecordMessage = schedule(s.Row.Channel, s.Programme);
    }

    /// <summary>Texto do botão de catch-up: "Desde o início" (a dar agora) ou "Ver do arquivo" (já passou).</summary>
    public string CatchupButtonText => Selected?.IsNow == true ? "Ver desde o início" : "Ver do arquivo";

    [RelayCommand]
    private void Watch(GuideRowViewModel? row)
    {
        if (row is null) RequestClose?.Invoke(this, null);
        else RequestClose?.Invoke(this, new GuideResult(row.Channel, null));
    }

    /// <summary>Duplo clique: programa que já passou e tem arquivo → catch-up; senão o canal em direto.</summary>
    public void Activate(ProgrammeBlockViewModel block) =>
        RequestClose?.Invoke(this, new GuideResult(block.Row.Channel, block.IsPast && block.CanCatchup ? block.Programme : null));

    /// <summary>Atualiza a linha "agora" (chamado por um temporizador da janela).</summary>
    public void Tick()
    {
        var span = (WindowEnd - WindowStart).TotalSeconds;
        NowFraction = (DateTime.Now - WindowStart).TotalSeconds / span;
    }

    partial void OnWindowStartChanged(DateTime value) => UpdateWindow();

    partial void OnOnlyWithEpgChanged(bool value) => ApplyFilter();

    partial void OnSelectedChanged(ProgrammeBlockViewModel? value)
    {
        RecordMessage = null;
        SelectedDescription = null;
        if (value is not null) _ = LoadDescriptionAsync(value);
    }

    private async Task LoadDescriptionAsync(ProgrammeBlockViewModel block)
    {
        var request = ++_descriptionRequest;
        try
        {
            var description = await _epgService.GetDescriptionAsync(_playlistId, block.Programme);
            if (request == _descriptionRequest) SelectedDescription = description ?? "Sem descrição.";
        }
        catch (Exception ex)
        {
            Log.Warn("Não foi possível ler a descrição do programa.", ex);
        }
    }

    private void ApplyFilter() =>
        Rows = OnlyWithEpg ? _allRows.Where(r => r.Channel.EpgId is not null).ToList() : _allRows;

    private void UpdateWindow()
    {
        Version++;
        var markers = new List<TimeMarker>();
        for (var t = WindowStart; t < WindowEnd; t = t.AddMinutes(30))
            markers.Add(new TimeMarker(t.ToString("HH:mm"), (t - WindowStart).TotalHours / WindowHours));
        Markers = markers;
        Tick();
        foreach (var row in _allRows) row.Invalidate();
    }

    private static DateTime RoundDown(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute < 30 ? 0 : 30, 0, t.Kind);
}

/// <summary>O que ver ao fechar o guia: o canal em direto, ou um programa do arquivo.</summary>
public sealed record GuideResult(ChannelItemViewModel Channel, EpgProgramme? Catchup);

public sealed record TimeMarker(string Label, double Start);

public sealed class GuideRowViewModel : ObservableObject
{
    private readonly GuideViewModel _owner;
    private IReadOnlyList<ProgrammeBlockViewModel>? _blocks;
    private int _version = -1;

    public GuideRowViewModel(ChannelItemViewModel channel, GuideViewModel owner)
    {
        Channel = channel;
        _owner = owner;
    }

    public ChannelItemViewModel Channel { get; }

    /// <summary>Calculado só quando a linha é desenhada (virtualização).</summary>
    public IReadOnlyList<ProgrammeBlockViewModel> Blocks
    {
        get
        {
            if (_blocks is not null && _version == _owner.Version) return _blocks;
            _version = _owner.Version;
            _blocks = Build();
            return _blocks;
        }
    }

    public bool HasProgrammes => Channel.EpgId is not null && Blocks.Count > 0;

    internal void Invalidate()
    {
        if (_blocks is null) return; // nunca foi desenhada: nada a atualizar
        _blocks = null;
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(HasProgrammes));
    }

    private IReadOnlyList<ProgrammeBlockViewModel> Build()
    {
        if (Channel.EpgId is null) return [];
        var start = _owner.WindowStartUnix;
        var end = _owner.WindowEndUnix;
        var span = (double)(end - start);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return _owner.GetProgrammes(Channel.EpgId).Select(p =>
        {
            var from = Math.Max(p.Start, start);
            var to = Math.Min(p.Stop, end);
            var catchup = Channel.Model.HasCatchup && p.Start <= now && p.Start >= now - Channel.Model.CatchupDays * 86400L;
            return new ProgrammeBlockViewModel(p, this, (from - start) / span, (to - from) / span,
                isNow: p.Start <= now && now < p.Stop, isPast: p.Stop <= now, canCatchup: catchup);
        }).ToList();
    }

    public override string ToString() => Channel.Name;
}

public sealed class ProgrammeBlockViewModel
{
    public ProgrammeBlockViewModel(EpgProgramme programme, GuideRowViewModel row, double start, double length,
        bool isNow, bool isPast, bool canCatchup = false)
    {
        CanCatchup = canCatchup;
        Programme = programme;
        Row = row;
        Start = start;
        Length = length;
        IsNow = isNow;
        IsPast = isPast;
    }

    public EpgProgramme Programme { get; }
    public GuideRowViewModel Row { get; }
    public double Start { get; }
    public double Length { get; }
    public bool IsNow { get; }
    public bool IsPast { get; }

    /// <summary>Pode ser visto do arquivo (canal com catch-up e programa dentro do período guardado).</summary>
    public bool CanCatchup { get; }
    public string Title => Programme.Title;
    public string TimeText => $"{Programme.StartLocal:HH:mm} – {Programme.StopLocal:HH:mm}";
    public string ChannelName => Row.Channel.Name;

    public override string ToString() => $"{Title} ({TimeText})";
}
