using System.Text;
using DSC.TLink.ITv2.Messages;

namespace CaptureTool;

/// <summary>
/// Replaces secrets in decoded messages: alarm codes, user/zone/partition names, the panel's
/// device ID. Every original secret is remembered so the audit can prove none survived.
///
/// Policy: a type is either known to carry no secrets (kept verbatim), known to carry secrets
/// in specific fields (rewritten here), or unknown (dropped). Nothing unknown is passed through.
/// </summary>
internal sealed class Redactor
{
    public HashSet<string> SecretStrings { get; } = new(StringComparer.Ordinal);
    public List<byte[]> SecretBytes { get; } = [];

    /// <summary>Message types that carry no secrets and are kept exactly as captured.</summary>
    private static readonly HashSet<Type> Harmless =
    [
        typeof(SimpleAck), typeof(CommandResponse), typeof(ConnectionPoll), typeof(ConnectionEndSession),
        typeof(RequestAccess), typeof(CommandRequestMessage), typeof(ConnectionSystemCapabilities),
        typeof(ModulePartitionStatus), typeof(ModuleZoneStatus),
        typeof(NotificationArmDisarm), typeof(NotificationExitDelay), typeof(NotificationPartitionReadyStatus),
        typeof(NotificationLifestyleZoneStatus), typeof(NotificationDateTimeBroadcast), typeof(ProgrammingLeadInOut),
        typeof(ConfigurationExit), typeof(SectionRead), typeof(SingleZoneBypassWrite), typeof(SingleZoneBypassStatus),
        typeof(AccessCodeReadRequest), typeof(UserCodeConfigurationReadRequest), typeof(UserCodeConfigurationReadResponse),
        typeof(AccessCodeAttributeReadRequest), typeof(AccessCodeAttributeReadResponse),
        typeof(AccessCodePartitionAssignmentReadRequest), typeof(AccessCodePartitionAssignmentReadResponse),
    ];

    /// <summary>Installer section groups (first address word) that hold no names or codes.</summary>
    private static bool IsHarmlessSection(ushort[] address) => address.Length > 0 && address[0] switch
    {
        1 => true,                      // zone definitions
        2 => true,                      // zone attributes
        >= 200 and <= 208 => true,      // partition enables, zone partition assignments
        307 => true,                    // zone event reporting
        _ => false,
    };

    public enum Verdict { Keep, Rewritten, Drop }

    public (Verdict Verdict, IMessageData Message) Redact(IMessageData message)
    {
        switch (message)
        {
            case PartitionArm m:
                return (Verdict.Rewritten, m with { AccessCode = Code(m.AccessCode) });
            case PartitionDisarm m:
                return (Verdict.Rewritten, m with { AccessCode = Code(m.AccessCode) });
            case ConfigurationEnter m:
                return (Verdict.Rewritten, m with { AccessCode = Code(m.AccessCode) });
            case AccessCodeReadResponse m:
                return (Verdict.Rewritten, m with { AccessCodes = m.AccessCodes.Select(Code).ToArray() });
            case NotificationLabelText m:
                return (Verdict.Rewritten, m with
                {
                    Labels = m.Labels.Select((label, i) => Label(label, m.Collection.ToString(), m.Start + i)).ToArray()
                });
            case OpenSession m:
                SecretBytes.Add(m.DeviceID);
                return (Verdict.Rewritten, m with { DeviceID = new byte[m.DeviceID.Length] });
            case SectionReadResponse m:
                return RedactSection(m);
            case MultipleMessagePacket m:
            {
                var parts = m.Messages.Select(Redact).ToList();
                if (parts.Any(p => p.Verdict == Verdict.Drop))
                    return (Verdict.Drop, m);
                return parts.All(p => p.Verdict == Verdict.Keep)
                    ? (Verdict.Keep, m)
                    : (Verdict.Rewritten, m with { Messages = parts.Select(p => p.Message).ToArray() });
            }
            default:
                return Harmless.Contains(message.GetType()) ? (Verdict.Keep, message) : (Verdict.Drop, message);
        }
    }

    private (Verdict, IMessageData) RedactSection(SectionReadResponse m)
    {
        var address = m.SectionAddress;
        if (IsHarmlessSection(address))
            return (Verdict.Keep, m);

        // Zone labels [0, 1, first] and partition labels [0, 100 + first]: UTF-16BE, fixed width each.
        string? kind = null;
        int first = 0;
        if (address is [0, 1, var zone]) { kind = "Zone"; first = zone; }
        else if (address is [0, var p] && p > 100) { kind = "Partition"; first = p - 100; }
        if (kind is null)
            return (Verdict.Drop, m);

        var data = m.SectionData;
        int count = Math.Max(1, (int)m.Count);
        int labelBytes = data.Length / count;
        var rewritten = new byte[data.Length];
        for (int i = 0; i < count; i++)
        {
            var original = Encoding.BigEndianUnicode.GetString(data, i * labelBytes, labelBytes).TrimEnd();
            var replacement = Label(original, kind, first + i).PadRight(labelBytes / 2);
            Encoding.BigEndianUnicode.GetBytes(replacement, 0, labelBytes / 2, rewritten, i * labelBytes);
        }
        m.SectionData = rewritten;
        return (Verdict.Rewritten, m);
    }

    /// <summary>Every digit becomes 0; length and any non-digit markers (blank slots) are kept.</summary>
    private string Code(string code)
    {
        if (code.Any(char.IsAsciiDigit) && code.Any(c => c != '0'))
            SecretStrings.Add(code);
        return new string(code.Select(c => char.IsAsciiDigit(c) ? '0' : c).ToArray());
    }

    private string Label(string original, string kind, int number)
    {
        var generic = $"{kind} {number:00}";
        var trimmed = original.Trim();
        if (trimmed.Length > 0 && !trimmed.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
            SecretStrings.Add(trimmed);
        // Keep the original width so fixed-width fields encode to the same size.
        return original.Length > generic.Length ? generic.PadRight(original.Length) : generic;
    }
}
