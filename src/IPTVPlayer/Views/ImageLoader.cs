using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace IPTVPlayer.Views;

/// <summary>
/// Propriedade anexada para carregar logótipos de forma assíncrona:
/// <c>&lt;Image views:ImageLoader.SourceUrl="{Binding LogoUrl}"/&gt;</c>.
/// Download com concorrência limitada, cache em disco, descodificação reduzida fora da UI,
/// cache em memória (LRU) e cancelamento quando o item é reciclado pela virtualização.
/// <c>ImageLoader.DecodeHeight</c> define a altura de descodificação (96 px por omissão; mais para capas).
/// </summary>
public static class ImageLoader
{
    private const int MaxBytes = 4 * 1024 * 1024;

    private static readonly SemaphoreSlim Throttle = new(6);
    private static readonly LruCache Cache = new(800);
    private static readonly ConcurrentDictionary<string, byte> Failed = new(StringComparer.Ordinal);
    private static HttpClient? _http;
    private static IPTVPlayer.Services.ImageDiskCache? _disk;

    public static readonly DependencyProperty DecodeHeightProperty = DependencyProperty.RegisterAttached(
        "DecodeHeight", typeof(int), typeof(ImageLoader), new PropertyMetadata(96));

    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.RegisterAttached(
        "SourceUrl", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnSourceUrlChanged));

    private static readonly DependencyProperty LoadCtsProperty = DependencyProperty.RegisterAttached(
        "LoadCts", typeof(CancellationTokenSource), typeof(ImageLoader));

    public static void Initialize(HttpClient http, IPTVPlayer.Services.ImageDiskCache disk)
    {
        _http = http;
        _disk = disk;
    }

    public static int GetDecodeHeight(DependencyObject d) => (int)d.GetValue(DecodeHeightProperty);

    public static void SetDecodeHeight(DependencyObject d, int value) => d.SetValue(DecodeHeightProperty, value);

    public static string? GetSourceUrl(DependencyObject d) => (string?)d.GetValue(SourceUrlProperty);

    public static void SetSourceUrl(DependencyObject d, string? value) => d.SetValue(SourceUrlProperty, value);

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;

        (image.GetValue(LoadCtsProperty) as CancellationTokenSource)?.Cancel();
        image.ClearValue(LoadCtsProperty);
        image.Source = null;

        var url = (e.NewValue as string)?.Trim();
        if (string.IsNullOrEmpty(url) || _http is null || Failed.ContainsKey(url)) return;

        var decodeHeight = GetDecodeHeight(image);
        var cacheKey = decodeHeight + "|" + url;
        if (Cache.TryGet(cacheKey, out var cached))
        {
            image.Source = cached;
            return;
        }

        var cts = new CancellationTokenSource();
        image.SetValue(LoadCtsProperty, cts);
        _ = LoadAsync(image, url, cacheKey, decodeHeight, cts.Token);
    }

    private static async Task LoadAsync(Image image, string url, string cacheKey, int decodeHeight, CancellationToken ct)
    {
        try
        {
            // Pequeno atraso: durante um scroll rápido, os itens que passam não chegam a descarregar.
            await Task.Delay(80, ct);
            var bitmap = await Task.Run(async () =>
            {
                await Throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await DownloadAndDecodeAsync(url, decodeHeight, ct).ConfigureAwait(false);
                }
                finally
                {
                    Throttle.Release();
                }
            }, ct);

            if (bitmap is null)
            {
                Failed.TryAdd(url, 0);
                return;
            }

            Cache.Add(cacheKey, bitmap);
            if (!ct.IsCancellationRequested) image.Source = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Logótipo em falta, formato não suportado (ex.: SVG), 404…: mostra-se as iniciais.
            Failed.TryAdd(url, 0);
        }
    }

    private static async Task<BitmapSource?> DownloadAndDecodeAsync(string url, int decodeHeight, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        byte[]? bytes;
        if (_disk?.TryRead(url) is { } fromDisk)
        {
            bytes = fromDisk;
        }
        else if (uri.IsFile)
        {
            bytes = await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false);
        }
        else if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        {
            using var response = await _http!.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length is > 0 and <= MaxBytes && LooksLikeImage(bytes)) _disk?.Write(url, bytes);
        }
        else
        {
            return null;
        }

        if (bytes.Length == 0 || bytes.Length > MaxBytes) return null;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelHeight = decodeHeight;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>PNG, JPEG, GIF, BMP, WebP ou ICO (evita guardar páginas de erro HTML em cache).</summary>
    private static bool LooksLikeImage(byte[] b) =>
        b.Length > 12 && (
            (b[0] == 0x89 && b[1] == 0x50) ||                       // PNG
            (b[0] == 0xFF && b[1] == 0xD8) ||                       // JPEG
            (b[0] == 0x47 && b[1] == 0x49) ||                       // GIF
            (b[0] == 0x42 && b[1] == 0x4D) ||                       // BMP
            (b[0] == 0x00 && b[1] == 0x00 && b[2] == 0x01) ||       // ICO
            (b[0] == 0x52 && b[1] == 0x49 && b[8] == 0x57 && b[9] == 0x45)); // RIFF....WEBP

    private sealed class LruCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Value)>> _map = new(StringComparer.Ordinal);
        private readonly LinkedList<(string Key, BitmapSource Value)> _list = new();
        private readonly object _sync = new();

        public LruCache(int capacity) => _capacity = capacity;

        public bool TryGet(string key, out BitmapSource? value)
        {
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }

        public void Add(string key, BitmapSource value)
        {
            lock (_sync)
            {
                if (_map.TryGetValue(key, out var existing)) _list.Remove(existing);
                var node = _list.AddFirst((key, value));
                _map[key] = node;
                if (_map.Count <= _capacity) return;

                var last = _list.Last!;
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
