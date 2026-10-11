using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace IPTVPlayer.Services.Epg;

/// <summary>Programa lido do XMLTV (horas em segundos Unix, UTC).</summary>
public sealed record XmltvProgramme(string EpgId, long Start, long Stop, string Title, string? Description);

/// <summary>Canal da playlist a associar ao EPG.</summary>
public sealed record EpgChannelRef(string Key, string? TvgId, string? TvgName, string Name);

/// <summary>Resultado de ler uma ou mais fontes XMLTV: ligação canal → id EPG e programas.</summary>
public sealed class XmltvResult
{
    /// <summary>Chave do canal da playlist → id do canal no XMLTV.</summary>
    public Dictionary<string, string> KeyToEpgId { get; } = new(StringComparer.Ordinal);
    public List<XmltvProgramme> Programmes { get; } = [];
}

/// <summary>
/// Leitor XMLTV em streaming (ficheiros de 100+ MB, com ou sem gzip). Só guarda os programas dos
/// canais que existem na playlist e dentro de uma janela de tempo, para poupar memória e disco.
/// Associação: pelo tvg-id; se não houver, pelo nome normalizado ("PT: RTP 1 HD" ↔ "RTP 1").
/// </summary>
public sealed partial class XmltvParser
{
    public static readonly TimeSpan KeepPast = TimeSpan.FromHours(3);
    public static readonly TimeSpan KeepFuture = TimeSpan.FromDays(4);

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "hd", "fhd", "uhd", "sd", "4k", "8k", "hevc", "h265", "h264", "1080p", "720p", "1080", "720", "backup", "hq", "lq", "tv",
    };

    private readonly Dictionary<string, List<string>> _keysByTvgId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _keysByName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _matchedByTvgId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wanted = new(StringComparer.Ordinal);
    private readonly long _minStop;
    private readonly long _maxStart;

    public XmltvParser(IEnumerable<EpgChannelRef> channels, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        _minStop = ToUnix(now - KeepPast);
        _maxStart = ToUnix(now + KeepFuture);

        foreach (var ch in channels)
        {
            if (!string.IsNullOrWhiteSpace(ch.TvgId)) Add(_keysByTvgId, ch.TvgId.Trim().ToLowerInvariant(), ch.Key);
            foreach (var n in new[] { ch.TvgName, ch.Name })
            {
                var norm = NormalizeName(n);
                if (norm.Length > 0) Add(_keysByName, norm, ch.Key);
            }
        }
    }

    public XmltvResult Result { get; } = new();

    /// <summary>Lê uma fonte (pode ser chamado várias vezes para juntar várias fontes).</summary>
    public void Parse(Stream stream, CancellationToken ct = default)
    {
        using var input = DecompressIfNeeded(stream);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CheckCharacters = false,
        };

        using var reader = XmlReader.Create(input, settings);
        var count = 0;
        try
        {
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "tv")
                throw new UserFacingException("O ficheiro de EPG não é XMLTV válido (falta o elemento <tv>).");

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if ((++count & 1023) == 0) ct.ThrowIfCancellationRequested();

                if (reader.LocalName == "channel") ReadChannel(reader);
                else if (reader.LocalName == "programme") ReadProgramme(reader);
            }
        }
        catch (XmlException ex)
        {
            // Ficheiro cortado a meio: aproveitar o que já foi lido, se for alguma coisa.
            if (Result.Programmes.Count == 0)
                throw new UserFacingException("O ficheiro de EPG não é XMLTV válido.", ex);
            Log.Warn("XMLTV com erros; usada a parte lida até ao erro.", ex);
        }
    }

    /// <summary>Ordena, completa horas de fim em falta e remove duplicados (várias fontes).</summary>
    public XmltvResult Complete()
    {
        var byChannel = Result.Programmes.GroupBy(p => p.EpgId, StringComparer.Ordinal);
        var completed = new List<XmltvProgramme>(Result.Programmes.Count);
        foreach (var group in byChannel)
        {
            var list = group.OrderBy(p => p.Start).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (i > 0 && p.Start == list[i - 1].Start) continue;
                if (p.Stop <= p.Start)
                {
                    var next = list.Skip(i + 1).FirstOrDefault(n => n.Start > p.Start);
                    p = p with { Stop = next?.Start ?? p.Start + 3600 };
                }
                if (p.Stop > _minStop && p.Start < _maxStart) completed.Add(p);
            }
        }
        Result.Programmes.Clear();
        Result.Programmes.AddRange(completed);
        return Result;
    }

    private void ReadChannel(XmlReader reader)
    {
        var id = reader.GetAttribute("id")?.Trim();
        if (string.IsNullOrEmpty(id) || reader.IsEmptyElement) return;
        var idLower = id.ToLowerInvariant();

        if (_keysByTvgId.TryGetValue(idLower, out var keys))
        {
            foreach (var key in keys)
            {
                Result.KeyToEpgId[key] = id;
                _matchedByTvgId.Add(key);
            }
            _wanted.Add(id);
        }

        using var sub = reader.ReadSubtree();
        sub.Read();
        // Atenção: ReadElementContentAsString já avança para o nó seguinte; não chamar Read() a seguir.
        while (!sub.EOF)
        {
            if (sub.NodeType != XmlNodeType.Element || sub.LocalName != "display-name")
            {
                sub.Read();
                continue;
            }
            var name = NormalizeName(sub.ReadElementContentAsString());
            if (name.Length == 0 || !_keysByName.TryGetValue(name, out var nameKeys)) continue;

            foreach (var key in nameKeys)
            {
                // O tvg-id tem sempre prioridade sobre a correspondência por nome.
                if (_matchedByTvgId.Contains(key) || Result.KeyToEpgId.ContainsKey(key)) continue;
                Result.KeyToEpgId[key] = id;
                _wanted.Add(id);
            }
        }
    }

    private void ReadProgramme(XmlReader reader)
    {
        var channel = reader.GetAttribute("channel")?.Trim();
        if (string.IsNullOrEmpty(channel)) return;

        // XMLTV sem <channel>: aceitar se o id coincidir com um tvg-id da playlist.
        if (!_wanted.Contains(channel) && _keysByTvgId.TryGetValue(channel.ToLowerInvariant(), out var keys))
        {
            foreach (var key in keys)
            {
                Result.KeyToEpgId[key] = channel;
                _matchedByTvgId.Add(key);
            }
            _wanted.Add(channel);
        }
        if (!_wanted.Contains(channel)) return;

        var start = ParseTime(reader.GetAttribute("start"));
        var stop = ParseTime(reader.GetAttribute("stop")) ?? 0;
        if (start is null || reader.IsEmptyElement) return;
        if ((stop > 0 && stop <= _minStop) || start >= _maxStart) return;

        string? title = null, desc = null;
        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            while (!sub.EOF)
            {
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "title" && title is null)
                    title = sub.ReadElementContentAsString().Trim();
                else if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "desc" && desc is null)
                    desc = sub.ReadElementContentAsString().Trim();
                else
                    sub.Read();
            }
        }

        if (string.IsNullOrEmpty(title)) return;
        Result.Programmes.Add(new XmltvProgramme(channel, start.Value, stop, title,
            string.IsNullOrEmpty(desc) ? null : desc));
    }

    /// <summary>"20261011180000 +0100" (segundos e fuso opcionais; sem fuso = UTC).</summary>
    internal static long? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        var digits = 0;
        while (digits < s.Length && char.IsAsciiDigit(s[digits])) digits++;
        if (digits < 12) return null;

        var format = digits >= 14 ? "yyyyMMddHHmmss" : "yyyyMMddHHmm";
        if (!DateTime.TryParseExact(s[..Math.Min(digits, 14)], format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
            return null;

        var offset = TimeSpan.Zero;
        var rest = s[digits..].Trim();
        if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-') &&
            int.TryParse(rest.AsSpan(1, 2), out var hh) && int.TryParse(rest.AsSpan(3, 2), out var mm))
        {
            offset = new TimeSpan(hh, mm, 0);
            if (rest[0] == '-') offset = -offset;
        }

        return new DateTimeOffset(local, offset).ToUnixTimeSeconds();
    }

    /// <summary>"PT: RTP 1 HD" → "rtp1"; "SIC Notícias (FHD)" → "sicnoticias".</summary>
    internal static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = CountryPrefix().Replace(name, "");
        s = Brackets().Replace(s, " ");
        s = TextNormalizer.ForSearch(s);

        var sb = new StringBuilder(s.Length);
        foreach (var token in NonAlnum().Split(s))
        {
            if (token.Length == 0 || NoiseTokens.Contains(token)) continue;
            sb.Append(token);
        }
        return sb.ToString();
    }

    internal static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static Stream DecompressIfNeeded(Stream stream)
    {
        var buffered = new BufferedStream(stream, 64 * 1024);
        var header = new byte[2];
        var read = buffered.Read(header, 0, 2);
        var prefixed = new PrefixedStream(header.AsSpan(0, read).ToArray(), buffered);
        return read == 2 && header[0] == 0x1F && header[1] == 0x8B
            ? new GZipStream(prefixed, CompressionMode.Decompress)
            : prefixed;
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    [GeneratedRegex(@"^\s*[A-Za-z]{2,3}\s*[:|]\s*")]
    private static partial Regex CountryPrefix();

    [GeneratedRegex(@"[\(\[].*?[\)\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    /// <summary>Stream que devolve primeiro uns bytes já lidos (para espreitar o cabeçalho gzip).</summary>
    private sealed class PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _position;

        public PrefixedStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < _prefix.Length)
            {
                var n = Math.Min(count, _prefix.Length - _position);
                Array.Copy(_prefix, _position, buffer, offset, n);
                _position += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
