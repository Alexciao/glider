using System.Net;

namespace Glider.Core.Art;

/// <summary>Downloads one image. Returns null for "no image there" (non-200, too big); throws on network failure.</summary>
public interface IArtFetcher
{
    Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken ct);
}

/// <summary>Which URLs the art cache may fetch, and how Epic's CDN is asked for a smaller picture.</summary>
public static class ArtSourcePolicy
{
    /// <summary>Public https hosts only: no credentials, no custom port, no IP literal, no single-label or local names.</summary>
    public static bool IsAllowed(Uri u)
    {
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length != 0 || u.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = u.IdnHost;
        return host.Contains('.', StringComparison.Ordinal) &&
               !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
               !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) &&
               !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Epic's image CDN resizes with <c>?w=&amp;resize=1&amp;quality=medium</c> (verified; without <c>resize=1</c> the size is ignored).</summary>
    public static bool IsEpicCdn(Uri u) =>
        u.Host.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase) ||
        u.Host.EndsWith(".unrealengine.com", StringComparison.OrdinalIgnoreCase) ||
        u.Host.EndsWith(".epicgamescdn.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The URL to request for <paramref name="width"/> px: the CDN resize query for Epic's CDN, the source itself elsewhere.</summary>
    public static Uri RequestFor(Uri source, int width, int? sourceWidth)
    {
        if (width <= 0 || !IsEpicCdn(source) || source.Query.Length > 0)
        {
            return source;
        }

        // Never ask the CDN to scale up.
        var w = sourceWidth is > 0 ? Math.Min(width, sourceWidth.Value) : width;
        return new UriBuilder(source) { Query = $"w={w}&resize=1&quality=medium" }.Uri;
    }
}

/// <summary>HttpClient fetcher: redirects are followed by hand (≤ 3) so every hop must pass <see cref="ArtSourcePolicy"/>.</summary>
public sealed class HttpArtFetcher : IArtFetcher, IDisposable
{
    private const int MaxRedirects = 3;
    private readonly HttpClient _http;

    public HttpArtFetcher(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Glider-art/1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("image/jpeg,image/png,image/webp,image/*;q=0.8");
    }

    public async Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!ArtSourcePolicy.IsAllowed(current))
            {
                return null;
            }

            using var resp = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (resp.Headers.Location is not { } next)
                {
                    return null;
                }

                current = next.IsAbsoluteUri ? next : new Uri(current, next);
                continue;
            }

            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden)
            {
                return null;
            }

            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > maxBytes)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream((int)Math.Min(resp.Content.Headers.ContentLength ?? 64 * 1024, maxBytes));
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > maxBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, n);
            }

            return ms.ToArray();
        }

        return null;
    }

    public void Dispose() => _http.Dispose();
}
