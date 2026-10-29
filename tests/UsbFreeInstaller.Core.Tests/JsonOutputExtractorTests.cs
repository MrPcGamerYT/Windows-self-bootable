using System.Text;
using System.Text.Json;
using UsbFreeInstaller.Core.Services;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class JsonOutputExtractorTests
{
    [Fact]
    public void ParseLastJsonObject_CleanJson_ParsesExpectedProperties()
    {
        const string stdout = """{"IsElevated":true,"DriveLetter":"C","DiskNumber":0}""";

        using JsonDocument doc = JsonOutputExtractor.ParseLastJsonObject(stdout);

        Assert.True(doc.RootElement.GetProperty("IsElevated").GetBoolean());
        Assert.Equal("C", doc.RootElement.GetProperty("DriveLetter").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("DiskNumber").GetInt32());
    }

    [Fact]
    public void ParseLastJsonObject_WithStrayProfileBannerAndBracesInsideStrings_ExtractsTrailingJsonObject()
    {
        const string noisyStdout = """
            Loading personal and system profiles took 612ms.
            WARNING: Custom module {Alpha} initialized.
            {"RootPath":"E:\\","Guid":"{11111111-2222-3333-4444-555555555555}","Note":"Escaped \"quote\" and {braces} inside string"}
            Transcript stopped.
            """;

        using JsonDocument doc = JsonOutputExtractor.ParseLastJsonObject(noisyStdout);

        Assert.Equal(@"E:\", doc.RootElement.GetProperty("RootPath").GetString());
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", doc.RootElement.GetProperty("Guid").GetString());
        Assert.Contains("{braces}", doc.RootElement.GetProperty("Note").GetString());
    }

    [Fact]
    public void ParseLastJsonObject_EmptyOrNonJson_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => JsonOutputExtractor.ParseLastJsonObject("   "));
        Assert.Throws<InvalidOperationException>(() => JsonOutputExtractor.ParseLastJsonObject("No JSON output here"));
    }

    [Fact]
    public void CreatePowerShellRequest_EnforcesUtf8ConsoleEncodingAndSilentProgress()
    {
        var req = ProcessRunner.CreatePowerShellRequest("Write-Output 'ok'");

        int idx = Array.IndexOf(req.Arguments.ToArray(), "-EncodedCommand");
        Assert.True(idx >= 0 && idx + 1 < req.Arguments.Count);

        string decoded = Encoding.Unicode.GetString(Convert.FromBase64String(req.Arguments[idx + 1]));
        Assert.Contains("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8", decoded);
        Assert.Contains("$ProgressPreference = 'SilentlyContinue'", decoded);
    }
}
