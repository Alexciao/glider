using System.Text;
using Glider.Core.Links;

namespace Glider.Core.Tests.Links;

public class ActivationMessageTests
{
    private static Task<(bool Ok, string? Link)> Read(params byte[] bytes) =>
        ActivationMessage.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

    [Fact]
    public async Task PlainActivationIsTheOriginalSingleByte()
    {
        var bytes = ActivationMessage.Encode(null);
        Assert.Equal([ActivationMessage.Activate], bytes);
        Assert.Equal((true, (string?)null), await Read(bytes));
        Assert.Equal(bytes, ActivationMessage.Encode(""));
    }

    [Theory]
    [InlineData("glider://launch/Fortnite")]
    [InlineData("com.epicgames.launcher://apps/fn%3Aabc%3AFortnite?action=launch&silent=true")]
    [InlineData("glider://launch/Fortnité")] // non-ASCII survives (the parser decides it is invalid, not the pipe)
    public async Task LinkRoundTrips(string link)
    {
        var bytes = ActivationMessage.Encode(link);
        Assert.Equal(ActivationMessage.OpenLink, bytes[0]);
        Assert.Equal((true, (string?)link), await Read(bytes));
    }

    [Fact]
    public async Task ALinkTooLongForTheFrameBecomesPlainActivation()
    {
        var bytes = ActivationMessage.Encode("glider://launch/" + new string('a', ActivationMessage.MaxLinkBytes));
        Assert.Equal([ActivationMessage.Activate], bytes);
        Assert.True((await Read(bytes)).Ok);
    }

    [Fact]
    public async Task GarbageIsDropped()
    {
        Assert.False((await Read()).Ok); // connection closed without a byte
        Assert.False((await Read(9)).Ok); // unknown kind
        Assert.False((await Read(ActivationMessage.OpenLink)).Ok); // no length
        Assert.False((await Read(ActivationMessage.OpenLink, 5)).Ok); // half a length
        Assert.False((await Read(ActivationMessage.OpenLink, 0, 0)).Ok); // empty link
        Assert.False((await Read(ActivationMessage.OpenLink, 5, 0, 1, 2)).Ok); // truncated payload
        Assert.False((await Read(ActivationMessage.OpenLink, 0xFF, 0xFF, 1)).Ok); // 65535 bytes announced
        Assert.False((await Read(ActivationMessage.OpenLink, 2, 0, 0xC3, 0x28)).Ok); // invalid UTF-8
    }

    [Fact]
    public async Task OnlyTheDeclaredLengthIsRead()
    {
        var link = Encoding.UTF8.GetBytes("glider://downloads");
        var frame = new byte[3 + link.Length + 4];
        frame[0] = ActivationMessage.OpenLink;
        frame[1] = (byte)link.Length;
        link.CopyTo(frame, 3);
        Array.Fill(frame, (byte)'x', 3 + link.Length, 4); // trailing junk
        Assert.Equal((true, (string?)"glider://downloads"), await Read(frame));
    }

    [Fact]
    public async Task AStalledSenderCanBeAbandoned()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var stream = new StallingStream([ActivationMessage.OpenLink, 20, 0, (byte)'g']);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ActivationMessage.ReadAsync(stream, cts.Token));
    }

    /// <summary>Returns the given bytes, then never completes a read (a client that connected and went silent).</summary>
    private sealed class StallingStream(byte[] head) : Stream
    {
        private int _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos < head.Length)
            {
                buffer.Span[0] = head[_pos++];
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
