using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// Cleans PowerShell's verbose '#&lt; CLIXML' XML-serialized error stream into readable plain text
/// so diagnostic logs and UI error banners never display raw XML tags or '_x000D__x000A_' escapes.
/// </summary>
public static partial class PowerShellCliXmlCleaner
{
    [GeneratedRegex(@"<S\s+S=""Error"">(?<msg>.*?)</S>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorElementRegex();

    [GeneratedRegex(@"_x(?<hex>[0-9A-Fa-f]{4})_", RegexOptions.CultureInvariant)]
    private static partial Regex HexEscapeRegex();

    public static string Clean(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return string.Empty;
        }

        string trimmed = rawError.Trim();
        if (!trimmed.Contains("CLIXML", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.Contains("<Objs", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeEscapes(trimmed);
        }

        MatchCollection matches = ErrorElementRegex().Matches(trimmed);
        if (matches.Count == 0)
        {
            return DecodeEscapes(trimmed);
        }

        var sb = new StringBuilder();
        foreach (Match match in matches)
        {
            string decoded = DecodeEscapes(WebUtility.HtmlDecode(match.Groups["msg"].Value));
            sb.Append(decoded);
        }

        var cleanLines = sb.ToString()
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) &&
                        !l.StartsWith("+ CategoryInfo", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("+ FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("+ ~~~", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("At line:", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return cleanLines.Length > 0
            ? string.Join(" ", cleanLines)
            : DecodeEscapes(trimmed).Trim();
    }

    private static string DecodeEscapes(string text) =>
        HexEscapeRegex().Replace(text, m =>
        {
            int code = Convert.ToInt32(m.Groups["hex"].Value, 16);
            return ((char)code).ToString();
        });
}
