using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UsbFreeInstaller.Core.Services.Updates;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class UpdateSafetyTests
{
    [Theory]
    [InlineData(StandaloneFlavor.X64, "win-x64-standalone")]
    [InlineData(StandaloneFlavor.X64Lightweight, "win-x64-lightweight")]
    [InlineData(StandaloneFlavor.Arm64, "win-arm64-standalone")]
    [InlineData(StandaloneFlavor.X86, "win-x86-32bit")]
    public async Task Check_SelectsExactRunningStandaloneFlavor(StandaloneFlavor flavor, string suffix)
    {
        byte[] executable = LauncherBytes(flavor, 1);
        using var http = ClientFor(flavor, executable);
        UpdateOffer? offer = await new GitHubReleaseUpdater(http).CheckAsync(flavor);
        Assert.NotNull(offer);
        Assert.Equal($"SWBI-SelfWindowsBootableInstaller-v9.9.9-{suffix}.exe", offer!.AssetName);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(executable)), offer.Sha256);
        Assert.Equal(flavor, offer.Flavor);
    }

    [Fact]
    public async Task Check_RejectsDuplicateChecksumsAndUntrustedReleaseUrl()
    {
        byte[] executable = LauncherBytes(StandaloneFlavor.X64, 1);
        using var duplicate = ClientFor(StandaloneFlavor.X64, executable, duplicateChecksum: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitHubReleaseUpdater(duplicate).CheckAsync(StandaloneFlavor.X64));
        using var untrusted = ClientFor(StandaloneFlavor.X64, executable, maliciousAssetUrl: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitHubReleaseUpdater(untrusted).CheckAsync(StandaloneFlavor.X64));
    }

    [Theory]
    [InlineData("v1.0.0-beta")]
    [InlineData("v01.2.3")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3/../../evil")]
    public void StableVersion_RejectsPreReleaseAndMalformedTags(string tag) =>
        Assert.False(GitHubReleaseUpdater.TryStableVersion(tag, out _));

    [Fact]
    public async Task Check_RejectsRedirectToAnUntrustedHost()
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/metadata");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GitHubReleaseUpdater(http).CheckAsync(StandaloneFlavor.X64));
    }

    [Fact]
    public void LauncherIdentity_UsesPayloadFlavorAndChecksPeArchitecture()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "renamed.exe");
            byte[] data = LauncherBytes(StandaloneFlavor.X64Lightweight, 2);
            File.WriteAllBytes(path, data);
            Assert.Equal(StandaloneFlavor.X64Lightweight, StandaloneLauncher.Read(path).Flavor);
            StandaloneLauncher.EnsureMatchesProcess(StandaloneFlavor.X64Lightweight, Architecture.X64);
            Assert.Throws<InvalidOperationException>(() => StandaloneLauncher.EnsureMatchesProcess(StandaloneFlavor.X64Lightweight, Architecture.X86));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(68, 2), 0x014c);
            File.WriteAllBytes(path, data);
            Assert.Throws<InvalidOperationException>(() => StandaloneLauncher.Read(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Download_VerifiesChecksumAndReportsProgressBeforeReplacingAnything()
    {
        string dir = TempDir();
        try
        {
            byte[] original = LauncherBytes(StandaloneFlavor.X64, 1);
            byte[] replacement = LauncherBytes(StandaloneFlavor.X64, 2);
            string path = Path.Combine(dir, "SWBI.exe");
            File.WriteAllBytes(path, original);
            using var http = ClientFor(StandaloneFlavor.X64, replacement);
            var updater = new GitHubReleaseUpdater(http);
            UpdateOffer offer = (await updater.CheckAsync(StandaloneFlavor.X64))!;
            var reports = new List<UpdateDownloadProgress>();
            UpdateApplyPlan plan = await updater.DownloadAsync(offer, StandaloneLauncher.Read(path), new InlineProgress<UpdateDownloadProgress>(reports.Add));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(replacement, File.ReadAllBytes(plan.StagedPath));
            Assert.Equal(100, reports.Last().Percent);
            Assert.False(File.Exists(plan.BackupPath));
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(plan.StagedPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Download_BadChecksumDeletesPartialAndKeepsOriginal()
    {
        string dir = TempDir();
        try
        {
            byte[] original = LauncherBytes(StandaloneFlavor.X64, 1);
            byte[] replacement = LauncherBytes(StandaloneFlavor.X64, 2);
            string path = Path.Combine(dir, "SWBI.exe");
            File.WriteAllBytes(path, original);
            using var http = ClientFor(StandaloneFlavor.X64, replacement, wrongChecksum: true);
            var updater = new GitHubReleaseUpdater(http);
            UpdateOffer offer = (await updater.CheckAsync(StandaloneFlavor.X64))!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => updater.DownloadAsync(offer, StandaloneLauncher.Read(path)));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Apply_RetainsBackupAndRestartsOnlyAfterVerification()
    {
        string dir = TempDir();
        try
        {
            (UpdateApplyPlan plan, byte[] original, byte[] replacement) = await CreatePlan(dir);
            string? restarted = null;
            await UpdateFileTransaction.ApplyAsync(plan, target => { restarted = target; return true; });
            Assert.Equal(plan.TargetPath, restarted);
            Assert.Equal(replacement, File.ReadAllBytes(plan.TargetPath));
            Assert.Equal(original, File.ReadAllBytes(plan.BackupPath));
            Assert.False(File.Exists(plan.StagedPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Apply_FailedRestartRestoresOriginalAndRetainsDownload()
    {
        string dir = TempDir();
        try
        {
            (UpdateApplyPlan plan, byte[] original, byte[] replacement) = await CreatePlan(dir);
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateFileTransaction.ApplyAsync(plan, _ => false));
            Assert.Equal(original, File.ReadAllBytes(plan.TargetPath));
            Assert.Equal(replacement, File.ReadAllBytes(plan.StagedPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Apply_ChangedOriginalIsNeverOverwritten()
    {
        string dir = TempDir();
        try
        {
            (UpdateApplyPlan plan, _, _) = await CreatePlan(dir);
            byte[] changed = LauncherBytes(StandaloneFlavor.X64, 3);
            File.WriteAllBytes(plan.TargetPath, changed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateFileTransaction.ApplyAsync(plan, _ => throw new Exception("should not run")));
            Assert.Equal(changed, File.ReadAllBytes(plan.TargetPath));
            Assert.False(File.Exists(plan.BackupPath));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static async Task<(UpdateApplyPlan, byte[], byte[])> CreatePlan(string dir)
    {
        byte[] original = LauncherBytes(StandaloneFlavor.X64, 1);
        byte[] replacement = LauncherBytes(StandaloneFlavor.X64, 2);
        string target = Path.Combine(dir, "SWBI.exe");
        string stage = Path.Combine(dir, ".swbi-update-abc.exe");
        string backup = Path.Combine(dir, ".swbi-backup-abc.exe");
        File.WriteAllBytes(target, original);
        File.WriteAllBytes(stage, replacement);
        return (new UpdateApplyPlan(target, stage, backup, await UpdateFileTransaction.HashFileAsync(target),
            await UpdateFileTransaction.HashFileAsync(stage), StandaloneFlavor.X64), original, replacement);
    }

    private static HttpClient ClientFor(StandaloneFlavor flavor, byte[] executable, bool duplicateChecksum = false,
        bool maliciousAssetUrl = false, bool wrongChecksum = false)
    {
        string suffix = flavor switch
        {
            StandaloneFlavor.X64 => "win-x64-standalone",
            StandaloneFlavor.X64Lightweight => "win-x64-lightweight",
            StandaloneFlavor.Arm64 => "win-arm64-standalone",
            StandaloneFlavor.X86 => "win-x86-32bit",
            _ => throw new ArgumentOutOfRangeException(nameof(flavor))
        };
        string assetName = $"SWBI-SelfWindowsBootableInstaller-v9.9.9-{suffix}.exe";
        const string prefix = "https://github.com/MrPcGamerYT/Windows-self-bootable/releases/download/v9.9.9/";
        string hash = wrongChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(executable));
        string sums = $"{hash}  {assetName}\n" + (duplicateChecksum ? $"{hash}  {assetName}\n" : string.Empty);
        string metadata = JsonSerializer.Serialize(new
        {
            draft = false, prerelease = false, tag_name = "v9.9.9",
            assets = new object[]
            {
                new { name = assetName, size = executable.Length, state = "uploaded", browser_download_url = maliciousAssetUrl ? "https://evil.example/exe" : prefix + assetName },
                new { name = "SHA256SUMS.txt", size = Encoding.UTF8.GetByteCount(sums), state = "uploaded", browser_download_url = prefix + "SHA256SUMS.txt" }
            }
        });
        return new HttpClient(new Handler(request =>
        {
            string url = request.RequestUri!.ToString();
            byte[] body = url.Contains("api.github.com", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(metadata) :
                url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(sums) : executable;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }));
    }

    private static byte[] LauncherBytes(StandaloneFlavor flavor, byte version)
    {
        byte[] data = new byte[160];
        data[0] = (byte)'M'; data[1] = (byte)'Z'; data[2] = version;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60, 4), 64);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(data, 64);
        ushort machine = flavor switch { StandaloneFlavor.X86 => 0x014c, StandaloneFlavor.Arm64 => 0xAA64, _ => 0x8664 };
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(68, 2), machine);
        Encoding.ASCII.GetBytes("SWBIPK01").CopyTo(data, 80);
        string prefix = flavor switch { StandaloneFlavor.X64 => "x64s", StandaloneFlavor.X64Lightweight => "x64l", StandaloneFlavor.Arm64 => "arm64", _ => "x86s" };
        Encoding.ASCII.GetBytes(prefix + "-1234abcd").CopyTo(data, 88);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(104, 4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(144, 8), 80);
        Encoding.ASCII.GetBytes("SWBIEOF1").CopyTo(data, 152);
        return data;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "swbi-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
