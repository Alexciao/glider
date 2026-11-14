using System.Buffers.Binary;
using System.Text;

namespace Glider.Core.Links;

/// <summary>
/// What a second Glider process sends the first one over the single-instance pipe. Wire format (backwards compatible with the
/// original 1-byte "come to the front" ping): <c>0x01</c> = activate; <c>0x02 &lt;len:u16 LE&gt; &lt;utf-8 link&gt;</c> = handle this link.
/// The receiver still validates the link with the strict parsers; the pipe is per-user but links originate from anywhere.
/// </summary>
public static class ActivationMessage
{
    public const byte Activate = 1;
    public const byte OpenLink = 2;
    public const int MaxLinkBytes = 4096;

    private static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>Plain activation (null link) or a link request. A link that can't be sent (too long, not text) falls back to plain activation.</summary>
    public static byte[] Encode(string? link)
    {
        if (string.IsNullOrEmpty(link))
        {
            return [Activate];
        }

        var payload = Encoding.UTF8.GetBytes(link);
        if (payload.Length > MaxLinkBytes)
        {
            return [Activate];
        }

        var buffer = new byte[3 + payload.Length];
        buffer[0] = OpenLink;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1, 2), (ushort)payload.Length);
        payload.CopyTo(buffer, 3);
        return buffer;
    }

    /// <summary>
    /// Reads one message. Returns false for garbage (unknown kind, oversized/truncated/invalid payload); the stream is then abandoned.
    /// <paramref name="link"/> is null for a plain activation.
    /// </summary>
    public static async Task<(bool Ok, string? Link)> ReadAsync(Stream stream, CancellationToken ct)
    {
        var kind = new byte[1];
        if (await ReadExactlyOrEndAsync(stream, kind, ct).ConfigureAwait(false) != 1)
        {
            return (false, null);
        }

        if (kind[0] == Activate)
        {
            return (true, null);
        }

        if (kind[0] != OpenLink)
        {
            return (false, null);
        }

        var lengthBytes = new byte[2];
        if (await ReadExactlyOrEndAsync(stream, lengthBytes, ct).ConfigureAwait(false) != 2)
        {
            return (false, null);
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        if (length is 0 or > MaxLinkBytes)
        {
            return (false, null);
        }

        var payload = new byte[length];
        if (await ReadExactlyOrEndAsync(stream, payload, ct).ConfigureAwait(false) != length)
        {
            return (false, null);
        }

        try
        {
            return (true, Strict.GetString(payload));
        }
        catch (ArgumentException)
        {
            return (false, null);
        }
    }

    private static async Task<int> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
