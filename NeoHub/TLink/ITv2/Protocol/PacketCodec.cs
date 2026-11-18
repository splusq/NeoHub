using DSC.TLink.ITv2.Encryption;
using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.ITv2.Protocol;

/// <summary>
/// ITv2 packets as pure functions:
/// [length 1–2 B][sender seq][receiver seq][command 2 B][payload][CRC16], then AES-ECB with
/// zero padding once a key exists.
/// </summary>
internal static class PacketCodec
{
    public static byte[] Encode(ITv2Packet packet)
    {
        var bytes = new List<byte> { packet.SenderSequence, packet.ReceiverSequence };
        bytes.AddRange(MessageFactory.SerializeMessage(packet.Message));
        ITv2Framing.AddFraming(bytes);
        return bytes.ToArray();
    }

    public static byte[] Protect(byte[] plaintext, byte[]? key)
        => key is null ? plaintext : ITv2Crypto.Encrypt(key, plaintext);

    /// <summary>Decrypts (if keyed), trims AES padding using the length field, checks the CRC and decodes.</summary>
    public static Result<ITv2Packet> Decode(byte[] payload, byte[]? key)
    {
        try
        {
            var plain = key is null ? payload : ITv2Crypto.Decrypt(key, payload);
            ReadOnlySpan<byte> span = TrimToFrame(plain);
            ITv2Framing.RemoveFraming(ref span);
            if (span.Length < 2)
                return Result<ITv2Packet>.Fail(TLinkErrorCode.PacketParseError, "Packet too short");
            var message = MessageFactory.DeserializeMessage(span[2..]);
            return new ITv2Packet(span[0], span[1], message);
        }
        catch (Exception ex)
        {
            return Result<ITv2Packet>.Fail(TLinkErrorCode.PacketParseError, $"Failed to parse ITv2 packet: {ex.Message}");
        }
    }

    public static ReadOnlySpan<byte> TrimToFrame(ReadOnlySpan<byte> plain)
    {
        if (plain.Length == 0)
            return plain;
        int header = (plain[0] & 0x80) != 0 ? 2 : 1;
        if (plain.Length < header)
            return plain;
        int length = header == 2 ? ((plain[0] & 0x7F) << 8) | plain[1] : plain[0];
        int total = header + length;
        return total <= plain.Length ? plain[..total] : plain;
    }
}
