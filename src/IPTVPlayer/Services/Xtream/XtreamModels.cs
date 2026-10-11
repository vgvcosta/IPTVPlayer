using System.Text.Json;
using System.Text.Json.Serialization;

namespace IPTVPlayer.Services.Xtream;

public sealed record XtreamCredentials(string Server, string Username, string Password);

/// <summary>Resultado do login.</summary>
public sealed record XtreamAccount(string Status, DateTime? ExpiresUtc, string StreamExtension, string? Timezone = null);

public sealed class XtreamUserInfo
{
    [JsonPropertyName("auth")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long Auth { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Status { get; set; }

    [JsonPropertyName("exp_date")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ExpDate { get; set; }

    [JsonPropertyName("allowed_output_formats")]
    public JsonElement AllowedOutputFormats { get; set; }
}

public sealed class XtreamCategory
{
    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? CategoryName { get; set; }
}

public sealed class XtreamVodStream
{
    [JsonPropertyName("name")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("stream_id")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long StreamId { get; set; }

    [JsonPropertyName("stream_icon")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? StreamIcon { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Rating { get; set; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("container_extension")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ContainerExtension { get; set; }

    [JsonPropertyName("year")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Year { get; set; }

    [JsonPropertyName("releasedate")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ReleaseDate { get; set; }
}

public sealed class XtreamSeries
{
    [JsonPropertyName("name")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("series_id")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long SeriesId { get; set; }

    [JsonPropertyName("cover")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Cover { get; set; }

    [JsonPropertyName("rating")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Rating { get; set; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("releaseDate")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ReleaseDate { get; set; }

    [JsonPropertyName("year")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Year { get; set; }
}

public sealed class XtreamLiveStream
{
    [JsonPropertyName("num")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long Num { get; set; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("stream_id")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long StreamId { get; set; }

    [JsonPropertyName("stream_icon")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? StreamIcon { get; set; }

    [JsonPropertyName("epg_channel_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? EpgChannelId { get; set; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? CategoryId { get; set; }

    /// <summary>1 = o canal tem catch-up.</summary>
    [JsonPropertyName("tv_archive")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long TvArchive { get; set; }

    /// <summary>Dias de catch-up.</summary>
    [JsonPropertyName("tv_archive_duration")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long TvArchiveDuration { get; set; }
}
