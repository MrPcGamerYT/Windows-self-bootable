using System.Text.Json;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// Safely parses JSON emitted at the end of a PowerShell script even if a system profile,
/// OEM provider, or warning banner wrote extra text lines earlier on stdout.
/// </summary>
public static class JsonOutputExtractor
{
    public static JsonDocument ParseLastJsonObject(string rawStandardOutput)
    {
        if (string.IsNullOrWhiteSpace(rawStandardOutput))
        {
            throw new InvalidOperationException("External script returned empty output when JSON was expected.");
        }

        string trimmed = rawStandardOutput.Trim().TrimStart('\uFEFF');

        // 1. Fast path: entire stdout is a single clean JSON object
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                return JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Fall through to line-by-line / balanced-brace extraction
            }
        }

        // 2. Reverse line-by-line scan for single-line ConvertTo-Json -Compress output
        string[] lines = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string candidate = lines[i].Trim().TrimStart('\uFEFF');
            if (candidate.StartsWith('{') && candidate.EndsWith('}'))
            {
                try
                {
                    return JsonDocument.Parse(candidate);
                }
                catch (JsonException)
                {
                    // Continue scanning earlier candidate lines
                }
            }
        }

        // 3. Multi-line JSON fallback: scan backwards from last '}' to matching balanced '{'
        int endBrace = trimmed.LastIndexOf('}');
        if (endBrace > 0)
        {
            for (int start = trimmed.LastIndexOf('{', endBrace); start >= 0; start = start > 0 ? trimmed.LastIndexOf('{', start - 1) : -1)
            {
                string slice = trimmed[start..(endBrace + 1)];
                try
                {
                    return JsonDocument.Parse(slice);
                }
                catch (JsonException)
                {
                    // Keep walking backwards to outer '{'
                }
            }
        }

        throw new InvalidOperationException("External script output did not contain a valid JSON object.");
    }
}
