using System.Text;
using System.Text.Json;
using DSC.TLink.ITv2.Messages;

namespace CaptureTool;

/// <summary>
/// Proves the fixtures hold no secret: every original code, name and device ID the redactor saw
/// is searched for in every encoding it could appear in (ASCII, UTF-16BE, BCD nibbles), both in
/// the raw frame bytes and in the JSON text, along with the panel's integration ID.
/// </summary>
internal static class Audit
{
    public static List<string> Run(IEnumerable<string> fixturePaths, Redactor redactor)
    {
        var needles = new List<(string What, byte[] Bytes)>();
        foreach (var secret in redactor.SecretStrings)
        {
            needles.Add(($"'{Mask(secret)}' (ascii)", Encoding.ASCII.GetBytes(secret)));
            needles.Add(($"'{Mask(secret)}' (utf-16be)", Encoding.BigEndianUnicode.GetBytes(secret)));
            if (secret.All(char.IsAsciiDigit) && secret.Length >= 4)
                needles.Add(($"'{Mask(secret)}' (bcd)", Bcd(secret)));
        }
        foreach (var bytes in redactor.SecretBytes.Where(b => b.Any(x => x != 0)))
            needles.Add(("device id", bytes));
        var integrationId = Environment.GetEnvironmentVariable("NEOHUB_PANEL_ID");
        if (!string.IsNullOrEmpty(integrationId))
        {
            needles.Add(("panel id (ascii)", Encoding.ASCII.GetBytes(integrationId)));
            needles.Add(("panel id (bcd)", Bcd(integrationId)));
        }

        var leaks = new List<string>();
        foreach (var path in fixturePaths)
        {
            // Frames are hex text, where any digit string turns up by chance; they are checked
            // as bytes below. Everything else in the JSON (types, labels, markers) is checked here.
            // Offsets ("ms") are numbers, not data. Secrets are matched as whole words, so a user
            // named "Neo" doesn't match "NeoHub" while "Neo" on its own still would.
            var text = System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(path), "\"frame\":\"[0-9A-F]*\"|\"ms\":\\d+", "");
            foreach (var secret in redactor.SecretStrings)
            {
                var word = System.Text.RegularExpressions.Regex.Match(text,
                    $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(secret)}(?![\p{{L}}\p{{N}}])");
                if (word.Success)
                    leaks.Add($"{Path.GetFileName(path)}: '{Mask(secret)}' in JSON text near \"{Context(text, secret)}\"");
            }
            if (integrationId is { Length: > 0 } && text.Contains(integrationId, StringComparison.Ordinal))
                leaks.Add($"{Path.GetFileName(path)}: panel id in JSON text");

            int lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("frame", out var frameProperty))
                    continue;
                var frame = Convert.FromHexString(frameProperty.GetString()!);
                foreach (var (what, bytes) in needles.Where(n => n.Bytes.Length >= 2))
                    if (frame.AsSpan().IndexOf(bytes) >= 0)
                        leaks.Add($"{Path.GetFileName(path)}:{lineNumber}: {what} in frame");
            }
        }
        return leaks;
    }

    private static byte[] Bcd(string digits)
    {
        var padded = digits.Length % 2 == 0 ? digits : digits + "0";
        return Enumerable.Range(0, padded.Length / 2)
            .Select(i => (byte)(((padded[2 * i] - '0') << 4) | (padded[2 * i + 1] - '0')))
            .ToArray();
    }

    /// <summary>The JSON property around a match, with the match itself masked.</summary>
    private static string Context(string text, string secret)
    {
        var i = text.IndexOf(secret, StringComparison.Ordinal);
        var from = Math.Max(0, i - 30);
        var snippet = text.Substring(from, Math.Min(text.Length - from, secret.Length + 60));
        return snippet.Replace(secret, Mask(secret)).Replace("\n", " ");
    }

    // Never print a secret in full, even in a failure message.
    private static string Mask(string s) => s.Length <= 2 ? "**" : s[0] + new string('*', s.Length - 2) + s[^1];
}
