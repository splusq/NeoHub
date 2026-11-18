namespace DSC.TLink.Tests;

/// <summary>
/// Pins the message codec against real panel traffic. Every test is a pure function of the
/// fixture bytes: no threads, no clock.
/// </summary>
public class CodecGoldenTests
{
    public static TheoryData<string> FixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in CapturedTraffic.Files())
            data.Add(Path.GetFileName(file));
        return data;
    }

    private static IReadOnlyList<FixtureRecord> Load(string name)
        => CapturedTraffic.Load(Path.Combine(CapturedTraffic.Directory, name));

    [Fact]
    public void FixturesArePresent()
        => Assert.NotEmpty(CapturedTraffic.Files());

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void EveryFrame_HasAValidChecksum_AndDecodesToItsRecordedType(string fixture)
    {
        foreach (var record in Load(fixture).Where(r => r.IsPacket))
        {
            var (_, _, message) = CapturedTraffic.Decode(record.FrameBytes);
            Assert.True(CapturedTraffic.TypeName(message) == record.Type, $"{record}: decoded as {CapturedTraffic.TypeName(message)}");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureFiles))]
    public void FramesMarkedRoundTrip_ReEncodeByteForByte(string fixture)
    {
        foreach (var record in Load(fixture).Where(r => r.IsPacket && r.RoundTrips == true))
        {
            var frame = record.FrameBytes;
            var (sender, receiver, message) = CapturedTraffic.Decode(frame);
            Assert.True(frame.AsSpan().SequenceEqual(CapturedTraffic.Encode(sender, receiver, message)),
                $"{record}: re-encoded bytes differ");
        }
    }

    /// <summary>
    /// Every packet the panel sent or NeoHub sent re-encodes to exactly the captured bytes.
    /// (This caught two codec bugs: DateTime fields were never written, and the panel sends
    /// some user-number fields two bytes wide even when the value fits in one.)
    /// </summary>
    [Fact]
    public void EveryCapturedFrame_RoundTrips()
    {
        var notExact = CapturedTraffic.AllPackets().Where(p => p.Record.RoundTrips != true).ToList();
        Assert.True(notExact.Count == 0,
            "Frames the codec can't reproduce: " + string.Join(", ", notExact.Select(p => $"{p.File} {p.Record}")));
    }

    [Fact]
    public void RedactedCodes_AreAllZero()
    {
        foreach (var (_, record) in CapturedTraffic.AllPackets().Where(p => p.Record.Redacted == true))
        {
            var (_, _, message) = CapturedTraffic.Decode(record.FrameBytes);
            var code = message switch
            {
                ITv2.Messages.PartitionArm m => m.AccessCode,
                ITv2.Messages.PartitionDisarm m => m.AccessCode,
                ITv2.Messages.ConfigurationEnter m => m.AccessCode,
                _ => null,
            };
            if (code is not null)
                Assert.True(code.All(c => c == '0'), $"{record}: code not redacted");
        }
    }
}
