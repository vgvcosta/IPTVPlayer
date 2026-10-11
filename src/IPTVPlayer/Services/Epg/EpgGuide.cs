namespace IPTVPlayer.Services.Epg;

/// <summary>Programa em memória (sem descrição, que é lida da BD só quando precisa).</summary>
public sealed class EpgProgramme
{
    public EpgProgramme(string epgId, long start, long stop, string title)
    {
        EpgId = epgId;
        Start = start;
        Stop = stop;
        Title = title;
    }

    public string EpgId { get; }
    public long Start { get; }
    public long Stop { get; }
    public string Title { get; }

    public DateTime StartLocal => DateTimeOffset.FromUnixTimeSeconds(Start).LocalDateTime;
    public DateTime StopLocal => DateTimeOffset.FromUnixTimeSeconds(Stop).LocalDateTime;
}

/// <summary>Índice do EPG de uma playlist para consultas rápidas (agora / a seguir / intervalo).</summary>
public sealed class EpgGuide
{
    public static readonly EpgGuide Empty = new(new Dictionary<string, string>(), new Dictionary<string, EpgProgramme[]>());

    private readonly Dictionary<string, EpgProgramme[]> _byEpgId;

    public EpgGuide(IReadOnlyDictionary<string, string> keyToEpgId, Dictionary<string, EpgProgramme[]> byEpgId)
    {
        KeyToEpgId = keyToEpgId;
        _byEpgId = byEpgId;
    }

    /// <summary>Chave do canal → id EPG.</summary>
    public IReadOnlyDictionary<string, string> KeyToEpgId { get; }

    public int ChannelCount => _byEpgId.Count;

    public int ProgrammeCount => _byEpgId.Values.Sum(a => a.Length);

    public (EpgProgramme? Now, EpgProgramme? Next) GetNowNext(string epgId, long nowUnix)
    {
        if (!_byEpgId.TryGetValue(epgId, out var list) || list.Length == 0) return (null, null);

        var i = IndexAtOrAfter(list, nowUnix);
        // i = primeiro programa que termina depois de agora.
        if (i >= list.Length) return (null, null);
        var current = list[i];
        return current.Start <= nowUnix
            ? (current, i + 1 < list.Length ? list[i + 1] : null)
            : (null, current);
    }

    /// <summary>Programas que se sobrepõem a [from, to).</summary>
    public IReadOnlyList<EpgProgramme> GetRange(string epgId, long from, long to)
    {
        if (!_byEpgId.TryGetValue(epgId, out var list)) return [];
        var result = new List<EpgProgramme>();
        for (var i = IndexAtOrAfter(list, from); i < list.Length && list[i].Start < to; i++)
            result.Add(list[i]);
        return result;
    }

    /// <summary>Índice do primeiro programa com Stop > t (pesquisa binária; a lista está ordenada).</summary>
    private static int IndexAtOrAfter(EpgProgramme[] list, long t)
    {
        int lo = 0, hi = list.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (list[mid].Stop <= t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
