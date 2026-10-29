using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Services.Updates;

public sealed record UpdateOffer(string Tag, string AssetName, Uri DownloadUrl, long SizeBytes, string Sha256, StandaloneFlavor Flavor);
public sealed record UpdateDownloadProgress(long DownloadedBytes, long TotalBytes)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Min(100, DownloadedBytes * 100 / TotalBytes);
}
public sealed record UpdateApplyPlan(string TargetPath, string StagedPath, string BackupPath, string OldSha256, string NewSha256, StandaloneFlavor Flavor);

/// <summary>Opt-in updates from the official GitHub release; the same-release checksum detects corruption, not publisher compromise.</summary>
public sealed class GitHubReleaseUpdater
{
    private const string OwnerRepo = "MrPcGamerYT/Windows-self-bootable";
    private const long MaxExeBytes = 256L * 1024 * 1024;
    private readonly HttpClient _http;

    public GitHubReleaseUpdater(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
    }

    public async Task<UpdateOffer?> CheckAsync(StandaloneFlavor flavor, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendAsync(new Uri($"https://api.github.com/repos/{OwnerRepo}/releases/latest"), cancellationToken).ConfigureAwait(false);
        byte[] metadata = await ReadBoundedAsync(response, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(metadata);
        JsonElement root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidOperationException("The latest GitHub release is not a stable published release.");
        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryStableVersion(tag, out Version? releaseVersion) || !Version.TryParse(SwbiBrand.CurrentVersion, out Version? current))
            throw new InvalidOperationException("The release version could not be verified.");
        if (releaseVersion <= current) return null;

        string suffix = flavor switch
        {
            StandaloneFlavor.X64 => "win-x64-standalone",
            StandaloneFlavor.X64Lightweight => "win-x64-lightweight",
            StandaloneFlavor.Arm64 => "win-arm64-standalone",
            StandaloneFlavor.X86 => "win-x86-32bit",
            _ => throw new InvalidOperationException("Unsupported standalone flavor.")
        };
        string assetName = $"SWBI-SelfWindowsBootableInstaller-{tag}-{suffix}.exe";
        JsonElement assets = root.GetProperty("assets");
        var executable = FindAsset(assets, tag, assetName, MaxExeBytes);
        var sums = FindAsset(assets, tag, "SHA256SUMS.txt", 64 * 1024);
        using HttpResponseMessage sumResponse = await SendAsync(sums.Url, cancellationToken).ConfigureAwait(false);
        byte[] manifestBytes = await ReadBoundedAsync(sumResponse, 64 * 1024, cancellationToken).ConfigureAwait(false);
        string hash = ParseChecksum(Encoding.UTF8.GetString(manifestBytes).TrimStart('\uFEFF'), assetName);
        return new UpdateOffer(tag, assetName, executable.Url, executable.Size, hash, flavor);
    }

    public async Task<UpdateApplyPlan> DownloadAsync(UpdateOffer offer, StandaloneLauncherIdentity launcher,
        IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (offer.Flavor != launcher.Flavor || StandaloneLauncher.Read(launcher.Path).Flavor != launcher.Flavor)
            throw new InvalidOperationException("The running launcher does not match the selected update.");
        string oldHash = await UpdateFileTransaction.HashFileAsync(launcher.Path, cancellationToken).ConfigureAwait(false);
        string directory = Path.GetDirectoryName(launcher.Path) ?? throw new InvalidOperationException("Launcher directory is missing.");
        string token = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(directory, $".swbi-update-{token}.exe");
        string backup = Path.Combine(directory, $".swbi-backup-{token}.exe");
        try
        {
            using HttpResponseMessage response = await SendAsync(offer.DownloadUrl, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != offer.SizeBytes)
                throw new InvalidOperationException("The update download length does not match the release metadata.");
            using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                written = checked(written + read);
                if (written > offer.SizeBytes || written > MaxExeBytes) throw new InvalidOperationException("The update download exceeds the expected size.");
                sha.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress(written, offer.SizeBytes));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (written != offer.SizeBytes || !CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), Convert.FromHexString(offer.Sha256)))
                throw new InvalidOperationException("The downloaded update failed SHA-256 or length verification.");
            output.Close();
            if (StandaloneLauncher.Read(stage).Flavor != launcher.Flavor)
                throw new InvalidOperationException("The downloaded update has the wrong standalone flavor.");
            return new UpdateApplyPlan(launcher.Path, stage, backup, oldHash, offer.Sha256, launcher.Flavor);
        }
        catch
        {
            try { File.Delete(stage); } catch { /* Preserve the original error. */ }
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            EnsureTrustedHost(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd($"SWBI/{SwbiBrand.CurrentVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            // Reject handlers that followed redirects behind our back to an untrusted endpoint.
            if (response.RequestMessage?.RequestUri is Uri effective) EnsureTrustedHost(effective);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirect == 5) throw new InvalidOperationException("The GitHub download redirected too many times or without a location.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidOperationException("The GitHub download could not be resolved.");
    }

    private static void EnsureTrustedHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
            throw new InvalidOperationException("The update URL is not a trusted GitHub HTTPS endpoint.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long n && n > limit) throw new InvalidOperationException("The GitHub response is too large.");
        using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (result.Length + read > limit) throw new InvalidOperationException("The GitHub response is too large.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    private static (Uri Url, long Size) FindAsset(JsonElement assets, string tag, string expectedName, long maxSize)
    {
        JsonElement[] matching = assets.EnumerateArray().Where(x => x.GetProperty("name").GetString() == expectedName).ToArray();
        if (matching.Length != 1) throw new InvalidOperationException($"The release must contain exactly one '{expectedName}' asset.");
        JsonElement asset = matching[0];
        long size = asset.GetProperty("size").GetInt64();
        string expectedUrl = $"https://github.com/{OwnerRepo}/releases/download/{tag}/{expectedName}";
        if (asset.GetProperty("state").GetString() != "uploaded" || size <= 0 || size > maxSize ||
            asset.GetProperty("browser_download_url").GetString() != expectedUrl)
            throw new InvalidOperationException($"The release asset '{expectedName}' is invalid.");
        return (new Uri(expectedUrl), size);
    }

    internal static string ParseChecksum(string manifest, string expectedName)
    {
        string? match = null;
        foreach (string line in manifest.Split('\n'))
        {
            Match parsed = Regex.Match(line.TrimEnd('\r'), @"^([a-fA-F0-9]{64})[ \t]+\*?([^\r\n]+)$", RegexOptions.CultureInvariant);
            if (!parsed.Success || parsed.Groups[2].Value != expectedName) continue;
            if (match is not null) throw new InvalidOperationException("The checksum manifest contains a duplicate asset entry.");
            match = parsed.Groups[1].Value.ToUpperInvariant();
        }
        return match ?? throw new InvalidOperationException("The checksum manifest does not identify the selected executable.");
    }

    internal static bool TryStableVersion(string tag, out Version? version)
    {
        version = null;
        return Regex.IsMatch(tag, @"^v(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant) &&
            Version.TryParse(tag[1..], out version);
    }
}
