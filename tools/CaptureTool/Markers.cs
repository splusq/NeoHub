using System.Globalization;

namespace CaptureTool;

/// <summary>A human note about what was happening ("arm stay from HomeKit").</summary>
internal sealed record Marker(DateTimeOffset At, string Label) : CaptureEvent(At);

internal static class Markers
{
    /// <summary>
    /// Lines of "2026-11-18T05:09:16Z label". Blank lines and lines starting with # are ignored.
    /// The file stays with the private capture; only labels and offsets reach the fixtures.
    /// </summary>
    public static IReadOnlyList<Marker> Read(string path)
        => File.ReadLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l =>
            {
                var space = l.IndexOf(' ');
                return new Marker(
                    DateTimeOffset.Parse(l[..space], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                    l[(space + 1)..].Trim());
            })
            .ToList();
}
