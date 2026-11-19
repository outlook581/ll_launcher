using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ll_launcher.Services;

internal static class WebsiteIconService
{
    private const int CacheCapacity = 192;
    private const int MaxIconBytes = 1024 * 1024;
    private static readonly SemaphoreSlim Downloads = new(4);
    private static readonly Dictionary<Uri, (Task<ImageSource?> Icon, DateTime Created)> Cache = new();
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseCookies = false,
        UseDefaultCredentials = false,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All
    });

    private const RegexOptions HtmlOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex HeadTags = new(
        """<!--.*?(?:-->|$)|<script\b[^>]*>.*?(?:</script\s*>|$)|<style\b[^>]*>.*?(?:</style\s*>|$)|</head\s*>|<(base|link)\b(?:[^>"']|"[^"]*"|'[^']*')*>""",
        HtmlOptions | RegexOptions.Singleline);
    private static readonly Regex Attributes = new("""\s+([^\s=/>]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))""", HtmlOptions);

    internal static Task<ImageSource?> GetIconAsync(Uri pageUri)
    {
        if (!pageUri.IsAbsoluteUri || pageUri.Scheme is not ("http" or "https"))
            return Task.FromResult<ImageSource?>(null);

        // Only the scheme, host and port belong to the site lookup. Bookmarked URLs can contain tokens.
        var origin = new Uri(pageUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
        lock (Cache)
        {
            if (Cache.TryGetValue(origin, out var entry) &&
                (!entry.Icon.IsCompleted || entry.Icon.IsCompletedSuccessfully && entry.Icon.Result != null ||
                 DateTime.UtcNow - entry.Created < TimeSpan.FromMinutes(5)))
                return entry.Icon;
            if (!Cache.ContainsKey(origin) && Cache.Count >= CacheCapacity)
            {
                var oldest = Cache.Where(p => p.Value.Icon.IsCompleted).OrderBy(p => p.Value.Created)
                    .Select(p => p.Key).FirstOrDefault();
                if (oldest == null) return Task.FromResult<ImageSource?>(null);
                Cache.Remove(oldest);
            }
            var icon = LoadAsync(origin);
            Cache[origin] = (icon, DateTime.UtcNow);
            return icon;
        }
    }

    private static async Task<ImageSource?> LoadAsync(Uri origin)
    {
        await Downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pageUri = origin;
            List<Uri> candidates = [];
            using (var response = await Client.GetAsync(origin, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
            {
                pageUri = response.RequestMessage?.RequestUri ?? origin;
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (response.IsSuccessStatusCode && (mediaType == null || mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)))
                {
                    using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    byte[] prefix = new byte[128 * 1024];
                    int count = await body.ReadAtLeastAsync(prefix, prefix.Length, false, timeout.Token).ConfigureAwait(false);
                    candidates = FindIconLinks(Encoding.UTF8.GetString(prefix, 0, count), pageUri);
                }
            }
            var iconUris = candidates.Distinct().Take(4).Append(new Uri(pageUri, "/favicon.ico"));
            // A site can redirect its homepage to a separate account or login service.
            bool sameSite = origin.IdnHost.Equals(pageUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                (origin.Port == pageUri.Port || origin.IsDefaultPort && pageUri.IsDefaultPort);
            if (!sameSite) iconUris = iconUris.Prepend(new Uri(origin, "/favicon.ico"));
            foreach (var uri in iconUris.Distinct())
            {
                if (await DownloadIconAsync(uri, timeout.Token).ConfigureAwait(false) is { } icon) return icon;
            }
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
        finally { Downloads.Release(); }
    }

    private static List<Uri> FindIconLinks(string html, Uri pageUri)
    {
        Uri? baseUri = null;
        List<string> hrefs = [];
        foreach (Match tag in HeadTags.Matches(html))
        {
            if (tag.Value.StartsWith("</", StringComparison.Ordinal)) break;
            if (!tag.Groups[1].Success) continue; // Skip comments and raw script/style text.
            string? href = null, rel = null;
            foreach (Match attribute in Attributes.Matches(tag.Value))
            {
                string name = attribute.Groups[1].Value;
                var value = attribute.Groups[2].Success ? attribute.Groups[2].Value :
                    attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value;
                if (name.Equals("href", StringComparison.OrdinalIgnoreCase)) href = WebUtility.HtmlDecode(value).Trim();
                if (name.Equals("rel", StringComparison.OrdinalIgnoreCase)) rel = value;
            }
            if (string.IsNullOrEmpty(href)) continue;
            if (tag.Groups[1].Value.Equals("base", StringComparison.OrdinalIgnoreCase))
            {
                if (baseUri == null && Uri.TryCreate(pageUri, href, out var resolved) &&
                    resolved.Scheme is "http" or "https" && resolved.UserInfo.Length == 0)
                    baseUri = resolved;
                continue;
            }
            var tokens = rel?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens != null && (tokens.Contains("icon", StringComparer.OrdinalIgnoreCase) ||
                tokens.Contains("apple-touch-icon", StringComparer.OrdinalIgnoreCase))) hrefs.Add(href);
        }

        List<Uri> icons = [];
        foreach (string href in hrefs)
            if (Uri.TryCreate(baseUri ?? pageUri, href, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0)
                icons.Add(uri);
        return icons;
    }

    private static async Task<ImageSource?> DownloadIconAsync(Uri uri, CancellationToken token)
    {
        try
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxIconBytes) return null;
            await response.Content.LoadIntoBufferAsync(MaxIconBytes, token).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
            var frame = decoder.Frames.Where(f => f.PixelWidth is > 0 and <= 1024 && f.PixelHeight is > 0 and <= 1024)
                .OrderBy(f => f.PixelWidth < 48).ThenBy(f => Math.Abs(f.PixelWidth - 48)).FirstOrDefault();
            if (frame == null) return null;
            double scale = Math.Min(1.0, 48.0 / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource source = scale < 1.0 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
            var icon = new WriteableBitmap(source);
            icon.Freeze();
            return icon;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or NotSupportedException or
            FormatException or ArgumentException or InvalidOperationException or COMException or OverflowException)
        {
            return null;
        }
    }
}
