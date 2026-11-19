using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;

namespace CaptureTool;

internal sealed record DecodedPacket(byte SenderSequence, byte ReceiverSequence, IMessageData Message, byte[] Frame);

/// <summary>
/// The same packet layout ITv2Session uses: [length (1–2 bytes)] [sender seq] [receiver seq]
/// [command + payload] [CRC16]. Decrypted inbound packets may carry AES zero padding after
/// the frame, which is trimmed using the length field.
/// </summary>
internal static class PacketCodec
{
    public static byte[] TrimToFrame(byte[] plaintext)
    {
        if (plaintext.Length == 0)
            return plaintext;
        int header, length;
        if ((plaintext[0] & 0x80) != 0)
        {
            header = 2;
            length = ((plaintext[0] & 0x7F) << 8) | plaintext[1];
        }
        else
        {
            header = 1;
            length = plaintext[0];
        }
        var total = header + length;
        return total <= plaintext.Length ? plaintext[..total] : plaintext;
    }

    public static DecodedPacket Decode(byte[] plaintext)
    {
        var frame = TrimToFrame(plaintext);
        ReadOnlySpan<byte> span = frame;
        ITv2Framing.RemoveFraming(ref span);
        var message = MessageFactory.DeserializeMessage(span[2..]);
        return new DecodedPacket(span[0], span[1], message, frame);
    }

    public static byte[] Encode(byte senderSequence, byte receiverSequence, IMessageData message)
    {
        var bytes = new List<byte> { senderSequence, receiverSequence };
        bytes.AddRange(MessageFactory.SerializeMessage(message));
        ITv2Framing.AddFraming(bytes);
        return bytes.ToArray();
    }

    public static string TypeName(IMessageData message)
        => message is DefaultMessage dm ? $"DefaultMessage({dm.Command})" : message.GetType().Name;
}
