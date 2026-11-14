using System.Net;
using Glider.Core.Art;

namespace Glider.Core.Tests.Art;

/// <summary>The real fetcher over a scripted HttpMessageHandler: redirects are followed by hand and every hop is checked.</summary>
public sealed class HttpArtFetcherTests
{
    private sealed class Script(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public async Task ReturnsTheBody_FollowingHttpsRedirects()
    {
        var script = new Script(u => u.AbsolutePath == "/a" ? Redirect("https://cdn2.epicgames.com/b") : Ok([1, 2, 3]));
        using var fetcher = new HttpArtFetcher(script);

        Assert.Equal([1, 2, 3], await fetcher.FetchAsync(new Uri("https://cdn1.epicgames.com/a"), 1000, CancellationToken.None));
        Assert.Equal(["https://cdn1.epicgames.com/a", "https://cdn2.epicgames.com/b"], script.Seen.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData("http://cdn2.epicgames.com/b")]
    [InlineData("https://127.0.0.1/b")]
    [InlineData("https://localhost/b")]
    [InlineData("file:///c:/windows/win.ini")]
    public async Task ARedirectToAnUnsafeTarget_IsNeverFollowed(string target)
    {
        var script = new Script(u => u.Host == "cdn1.epicgames.com" ? Redirect(target) : Ok([9]));
        using var fetcher = new HttpArtFetcher(script);

        Assert.Null(await fetcher.FetchAsync(new Uri("https://cdn1.epicgames.com/a"), 1000, CancellationToken.None));
        Assert.Single(script.Seen); // only the original request was made
    }

    [Fact]
    public async Task RedirectLoops_AreCutOff()
    {
        var script = new Script(_ => Redirect("https://cdn1.epicgames.com/loop"));
        using var fetcher = new HttpArtFetcher(script);

        Assert.Null(await fetcher.FetchAsync(new Uri("https://cdn1.epicgames.com/loop"), 1000, CancellationToken.None));
        Assert.Equal(4, script.Seen.Count); // the request and three redirects
    }

    [Fact]
    public async Task NotFound_IsNull_ButAServerErrorThrows_SoItIsRetriedLater()
    {
        using var fetcher = new HttpArtFetcher(new Script(u => new HttpResponseMessage(u.AbsolutePath == "/missing" ? HttpStatusCode.NotFound : HttpStatusCode.BadGateway)));

        Assert.Null(await fetcher.FetchAsync(new Uri("https://cdn1.epicgames.com/missing"), 1000, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => fetcher.FetchAsync(new Uri("https://cdn1.epicgames.com/down"), 1000, CancellationToken.None));
    }

    [Fact]
    public async Task ABodyOverTheCap_IsRefused_WhetherOrNotItIsAnnounced()
    {
        using var announced = new HttpArtFetcher(new Script(_ => Ok(new byte[5000])));
        Assert.Null(await announced.FetchAsync(new Uri("https://cdn1.epicgames.com/big"), 1000, CancellationToken.None));

        // Chunked: no Content-Length, the cap is enforced while reading.
        using var chunked = new HttpArtFetcher(new Script(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[5000])) }));
        Assert.Null(await chunked.FetchAsync(new Uri("https://cdn1.epicgames.com/big"), 1000, CancellationToken.None));
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
