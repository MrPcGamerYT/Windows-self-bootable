using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services;

public sealed record ParsedWimMetadata(
    string PrimaryArchitecture,
    string PrimaryVersion,
    int PrimaryBuildNumber,
    IReadOnlyList<WimEditionInfo> Editions);

/// <summary>
/// Reads the uncompressed UTF-16LE &lt;WIM&gt; XML manifest directly from the binary header of any
/// .wim, .esd (LZMS solid WIM), or .swm (first split chunk) stream in a few milliseconds —
/// without requiring Mount-DiskImage, Administrator rights, or spawning dism.exe.
/// Works identically for Official Microsoft ISOs and Custom/Lite/Modified Windows 10/11 ISOs.
/// </summary>
public static class WimHeaderXmlParser
{
    private static ReadOnlySpan<byte> WimMagic => "MSWIM\0\0\0"u8;

    /// <summary>
    /// Size of WIMHEADER_V1_PACKED up through rhXmlData (offset 72 + 24 bytes = 96 bytes minimum; full header is 208 bytes).
    /// </summary>
    private const int MinWimHeaderBytes = 104;

    /// <summary>
    /// Safety cap for embedded WIM XML metadata (16 MiB). Real WIM XML manifests are typically 4 KB – 256 KB.
    /// </summary>
    private const int MaxXmlMetadataBytes = 16 * 1024 * 1024;

    public static bool TryParse(Stream wimStream, out ParsedWimMetadata? metadata)
    {
        metadata = null;
        if (wimStream is null || !wimStream.CanRead || !wimStream.CanSeek || wimStream.Length < MinWimHeaderBytes)
        {
            return false;
        }

        try
        {
            wimStream.Position = 0;
            Span<byte> header = stackalloc byte[MinWimHeaderBytes];
            ReadExactly(wimStream, header);

            if (!header[..8].SequenceEqual(WimMagic))
            {
                return false;
            }

            // Offset 72 in WIMHEADER_V1_PACKED is RESHDR_DISK_SHORT rhXmlData (24 bytes):
            //   [0..7]  : low 56 bits = compressed/raw byte size, top 8 bits = RESHDR_FLAG_*
            //   [8..15] : int64 file offset of XML data inside the WIM/ESD/SWM
            //   [16..23]: int64 original uncompressed byte size
            ulong sizeAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(72, 8));
            long xmlSizeBytes = (long)(sizeAndFlags & 0x00FF_FFFF_FFFF_FFFFUL);
            long xmlOffset = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(80, 8));

            if (xmlOffset <= 0 || xmlSizeBytes <= 4 || xmlSizeBytes > MaxXmlMetadataBytes || xmlOffset + xmlSizeBytes > wimStream.Length)
            {
                return false;
            }

            byte[] xmlBytes = new byte[xmlSizeBytes];
            wimStream.Position = xmlOffset;
            ReadExactly(wimStream, xmlBytes);

            string xmlText = DecodeWimXmlString(xmlBytes);
            int firstBracket = xmlText.IndexOf('<');
            if (firstBracket > 0)
            {
                xmlText = xmlText[firstBracket..];
            }

            metadata = ParseXmlDocument(xmlText);
            return metadata.Editions.Count > 0 || !string.Equals(metadata.PrimaryArchitecture, "Unknown", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            metadata = null;
            return false;
        }
    }

    public static ParsedWimMetadata ParseXmlDocument(string xmlText)
    {
        XDocument doc = XDocument.Parse(xmlText.Trim('\0', '\uFEFF', ' ', '\r', '\n', '\t'));
        XElement? root = doc.Root;
        if (root is null)
        {
            return new ParsedWimMetadata("Unknown", string.Empty, 0, Array.Empty<WimEditionInfo>());
        }

        var editions = new List<WimEditionInfo>();
        string primaryArch = "Unknown";
        string primaryVersion = string.Empty;
        int primaryBuild = 0;

        foreach (XElement imageEl in ElementsLocal(root, "IMAGE"))
        {
            int index = 1;
            string? idxAttr = imageEl.Attribute("INDEX")?.Value
                              ?? imageEl.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("INDEX", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(idxAttr))
            {
                _ = int.TryParse(idxAttr.Trim(), out index);
            }

            string rawName = ValueLocal(imageEl, "DISPLAYNAME")
                             ?? ValueLocal(imageEl, "NAME")
                             ?? ValueLocal(imageEl, "DESCRIPTION")
                             ?? $"Windows Image {index}";

            string rawDesc = ValueLocal(imageEl, "DISPLAYDESCRIPTION")
                             ?? ValueLocal(imageEl, "DESCRIPTION")
                             ?? rawName;

            long totalBytes = 0L;
            if (ValueLocal(imageEl, "TOTALBYTES") is { } tbStr)
            {
                _ = long.TryParse(tbStr.Trim(), out totalBytes);
            }

            string imgArch = "Unknown";
            string imgVersion = string.Empty;
            int imgBuild = 0;

            XElement? winEl = ElementsLocal(imageEl, "WINDOWS").FirstOrDefault();
            if (winEl is not null)
            {
                if (ValueLocal(winEl, "ARCH") is { } archCode)
                {
                    imgArch = archCode.Trim() switch
                    {
                        "9" => "x64",
                        "12" => "arm64",
                        "0" => "x86",
                        "5" => "arm",
                        "6" => "ia64",
                        var other => other
                    };
                }

                if (string.IsNullOrWhiteSpace(rawName) || rawName.StartsWith("Windows Image", StringComparison.OrdinalIgnoreCase))
                {
                    string? editionId = ValueLocal(winEl, "EDITIONID");
                    if (!string.IsNullOrWhiteSpace(editionId))
                    {
                        rawName = $"Windows ({editionId.Trim()})";
                    }
                }

                XElement? verEl = ElementsLocal(winEl, "VERSION").FirstOrDefault();
                if (verEl is not null)
                {
                    string major = ValueLocal(verEl, "MAJOR") ?? "10";
                    string minor = ValueLocal(verEl, "MINOR") ?? "0";
                    string buildStr = ValueLocal(verEl, "BUILD") ?? "0";
                    _ = int.TryParse(buildStr.Trim(), out imgBuild);
                    imgVersion = $"{major.Trim()}.{minor.Trim()}.{buildStr.Trim()}";
                }
            }

            // Fallback build/arch heuristics for heavily stripped custom WIMs that omit <WINDOWS> node
            if (imgBuild == 0)
            {
                imgBuild = InferBuildFromText(rawName + " " + rawDesc);
                if (imgBuild > 0 && string.IsNullOrEmpty(imgVersion))
                {
                    imgVersion = $"10.0.{imgBuild}";
                }
            }

            if (string.Equals(imgArch, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                imgArch = InferArchFromText(rawName + " " + rawDesc);
            }

            if (primaryBuild == 0 && imgBuild > 0)
            {
                primaryBuild = imgBuild;
            }

            if (string.IsNullOrEmpty(primaryVersion) && !string.IsNullOrEmpty(imgVersion))
            {
                primaryVersion = imgVersion;
            }

            if (string.Equals(primaryArch, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(imgArch, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                primaryArch = imgArch;
            }

            editions.Add(new WimEditionInfo
            {
                Index = index,
                Name = rawName.Trim(),
                Description = rawDesc.Trim(),
                Architecture = string.Equals(imgArch, "Unknown", StringComparison.OrdinalIgnoreCase) ? primaryArch : imgArch,
                SizeBytes = totalBytes,
                Version = imgVersion
            });
        }

        // Backfill architecture/version if discovered on a later image index
        if (!string.Equals(primaryArch, "Unknown", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(primaryVersion))
        {
            editions = editions.Select(e => e with
            {
                Architecture = string.Equals(e.Architecture, "Unknown", StringComparison.OrdinalIgnoreCase) ? primaryArch : e.Architecture,
                Version = string.IsNullOrEmpty(e.Version) ? primaryVersion : e.Version
            }).ToList();
        }

        return new ParsedWimMetadata(primaryArch, primaryVersion, primaryBuild, editions);
    }

    private static string DecodeWimXmlString(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        // WIM XML without BOM: check if odd bytes are mostly 0x00 (UTF-16LE)
        if (bytes.Length >= 4 && bytes[1] == 0x00 && bytes[3] == 0x00)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static IEnumerable<XElement> ElementsLocal(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static string? ValueLocal(XElement parent, string localName) =>
        ElementsLocal(parent, localName).FirstOrDefault()?.Value;

    private static int InferBuildFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (text.Contains("25H2", StringComparison.OrdinalIgnoreCase)) return 26200;
        if (text.Contains("24H2", StringComparison.OrdinalIgnoreCase)) return 26100;
        if (text.Contains("23H2", StringComparison.OrdinalIgnoreCase)) return 22631;
        if (text.Contains("22H2", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("11", StringComparison.OrdinalIgnoreCase) || text.Contains("Tiny11", StringComparison.OrdinalIgnoreCase)))
        {
            return 22621;
        }

        if (text.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Win11", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("W11", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tiny11", StringComparison.OrdinalIgnoreCase))
        {
            return 22631;
        }

        if (text.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Win10", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("W10", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tiny10", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("21H2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("22H2", StringComparison.OrdinalIgnoreCase))
        {
            return 19045;
        }

        return 0;
    }

    private static string InferArchFromText(string text)
    {
        if (text.Contains("arm64", StringComparison.OrdinalIgnoreCase) || text.Contains("aarch64", StringComparison.OrdinalIgnoreCase))
        {
            return "arm64";
        }

        if (text.Contains("x64", StringComparison.OrdinalIgnoreCase) || text.Contains("amd64", StringComparison.OrdinalIgnoreCase) || text.Contains("64-bit", StringComparison.OrdinalIgnoreCase))
        {
            return "x64";
        }

        if (text.Contains("x86", StringComparison.OrdinalIgnoreCase) || text.Contains("32-bit", StringComparison.OrdinalIgnoreCase))
        {
            return "x86";
        }

        return "Unknown";
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n <= 0)
            {
                throw new EndOfStreamException("Unexpected end of WIM stream.");
            }

            total += n;
        }
    }
}
