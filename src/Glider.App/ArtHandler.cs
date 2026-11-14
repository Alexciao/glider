using System.Diagnostics;
using Glider.Core.Art;
using Microsoft.Web.WebView2.Core;

namespace Glider.App;

/// <summary>
/// Serves <c>https://art.glider/{app}/{tall|tall-thumb|wide|logo}?v=…</c> from Core's <see cref="ArtCache"/> (first request downloads and
/// stores the picture, later ones are disk hits; a miss that can't be filled is an immediate 404 so a tile never hangs).
/// <c>art.glider</c> is deliberately not a mapped virtual host: those never raise <c>WebResourceRequested</c> (docs/decisions/host.md).
/// Runs on the UI thread; only the awaits leave it.
/// </summary>
internal static class ArtHandler
{
    // The page lives on app.glider, art on art.glider: allow it to read the pictures (fetch / canvas with crossorigin) and nothing else.
    private const string Cors = "Access-Control-Allow-Origin: https://app.glider";
    private const string NotFoundHeaders = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" + Cors;

    public static void Attach(CoreWebView2 web, Func<Task<ArtCache?>> getCache)
    {
        web.AddWebResourceRequestedFilter(ArtUrls.Prefix + "*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (!uri.StartsWith(ArtUrls.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var deferral = e.GetDeferral();
            _ = RespondAsync(web, e, uri, getCache, deferral);
        };
    }

    private static async Task RespondAsync(CoreWebView2 web, CoreWebView2WebResourceRequestedEventArgs e, string uri, Func<Task<ArtCache?>> getCache, CoreWebView2Deferral deferral)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var cache = await getCache();
            var response = cache is null ? ArtResponse.NotFound : await cache.HandleAsync(uri, CancellationToken.None);
            // Back on the UI thread (the WPF sync context): the response object must be created here.
            e.Response = Build(web, response);
            if (response.Status != 200)
            {
                HostLog.Info($"art {response.Status} {uri} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            }
        }
        catch (Exception ex)
        {
            HostLog.Warn($"art request failed: {uri}", ex);
            try
            {
                e.Response = web.Environment.CreateWebResourceResponse(null, 404, "Not Found", NotFoundHeaders);
            }
            catch (Exception inner) when (inner is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static CoreWebView2WebResourceResponse Build(CoreWebView2 web, ArtResponse r)
    {
        if (r.File is not { } file)
        {
            return web.Environment.CreateWebResourceResponse(null, r.Status, r.Status == 400 ? "Bad Request" : "Not Found", NotFoundHeaders);
        }

        try
        {
            var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return web.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                $"Content-Type: {file.ContentType}\r\nCache-Control: {r.CacheControl}\r\nX-Content-Type-Options: nosniff\r\nContent-Length: {file.Length}\r\n{Cors}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return web.Environment.CreateWebResourceResponse(null, 404, "Not Found", NotFoundHeaders);
        }
    }
}
