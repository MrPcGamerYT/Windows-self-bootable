using System.Diagnostics;
using System.Text;
using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

/// <summary>
/// 100% Self-Contained Pure C# + Native wimgapi.dll PC Storage & Network Driver Harvester.
/// Scans %SystemRoot%\System32\DriverStore\FileRepository directly in C#, exports third-party OEM
/// storage (Intel VMD/RST, AMD RAID, NVMe, VirtIO, SCSI) & network drivers to $WinPEDriver$ and
/// SWBI's Driver Chooser folder, and injects storage drivers into WinPE boot.wim with ZERO reliance
/// on powershell.exe, pnputil.exe, or dism.exe.
/// </summary>
public sealed class WindowsDriverHarvesterService : IDriverHarvesterService
{
    private readonly IProcessRunner _processRunner;
    private readonly ISafeLogger _logger;

    public WindowsDriverHarvesterService(IProcessRunner processRunner, ISafeLogger logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<DriverHarvestSummary> ExportAndInjectDriversAsync(
        string installerDriveLetter,
        bool includeNetworkDrivers,
        bool injectStorageIntoBootWim,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string cleanLetter = installerDriveLetter.TrimEnd(':', '\\', '/');
        string rootDir = $"{cleanLetter}:{Path.DirectorySeparatorChar}";

        string winPeDriverRoot = Path.Combine(rootDir, "$WinPEDriver$");
        string storageOutDir = Path.Combine(winPeDriverRoot, "Storage");
        string networkOutDir = Path.Combine(winPeDriverRoot, "Network");

        string swbiChooserDriverRoot = Path.Combine(rootDir, SwbiBrand.DriverChooserFolderName);
        string swbiChooserStorageDir = Path.Combine(swbiChooserDriverRoot, "Storage");
        string swbiChooserNetDir = Path.Combine(swbiChooserDriverRoot, "Network");

        Directory.CreateDirectory(storageOutDir);
        Directory.CreateDirectory(swbiChooserStorageDir);
        if (includeNetworkDrivers)
        {
            Directory.CreateDirectory(networkOutDir);
            Directory.CreateDirectory(swbiChooserNetDir);
        }

        progress?.Report(new DeploymentProgress(
            "SWBI: Harvesting PC Storage & Network Drivers",
            20,
            $"Scanning Windows DriverStore directly in C# for OEM storage & network drivers..."));

        string driverStoreRepo = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "DriverStore",
            "FileRepository");

        int storageCount = 0;
        int netCount = 0;

        await Task.Run(() =>
        {
            storageCount = NativeDriverStoreScanner.ExportBootCriticalStorageDrivers(
                driverStoreRepo,
                new[] { storageOutDir, swbiChooserStorageDir },
                activeControllerServiceName: null,
                _logger);

            if (includeNetworkDrivers)
            {
                netCount = NativeDriverStoreScanner.ExportOemNetworkDrivers(
                    driverStoreRepo,
                    new[] { networkOutDir, swbiChooserNetDir },
                    _logger);
            }
        }, cancellationToken).ConfigureAwait(false);

        await WriteSwbiDriverReadMeAsync(swbiChooserDriverRoot, storageCount, netCount, cancellationToken).ConfigureAwait(false);

        long exportedBytes = ComputeDirectoryTotalBytes(winPeDriverRoot);
        bool injectedBootWim = false;

        if (injectStorageIntoBootWim && storageCount > 0 && OperatingSystem.IsWindows())
        {
            progress?.Report(new DeploymentProgress(
                "SWBI: Staging Storage Drivers in WinPE boot.wim",
                65,
                "Updating sources\\boot.wim in-process via wimgapi.dll..."));

            string bootWimPath = Path.Combine(rootDir, "sources", "boot.wim");
            injectedBootWim = await Task.Run(
                () => NativeWimApi.CustomizeBootWimInProcess(
                    bootWimPath,
                    applyWin11HardwareBypass: false,
                    storageDriversSourceDir: storageOutDir,
                    _logger),
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new DeploymentProgress(
            "SWBI: Driver Harvest Complete",
            100,
            $"Exported {storageCount} storage + {netCount} network OEM package(s) ({ByteFormatting.FormatGb(exportedBytes)})."));

        return new DriverHarvestSummary(storageCount, netCount, exportedBytes, injectedBootWim);
    }

    public Task LaunchInPlaceUpgradeSetupAsync(
        string installerDriveLetter,
        CancellationToken cancellationToken = default)
    {
        string cleanLetter = installerDriveLetter.TrimEnd(':', '\\', '/');
        string setupExe = Path.Combine($"{cleanLetter}:{Path.DirectorySeparatorChar}", "setup.exe");

        if (!File.Exists(setupExe))
        {
            throw new FileNotFoundException($"Windows Setup executable was not found at '{setupExe}'.");
        }

        _logger.Info("Upgrade", $"Launching SWBI in-place Windows Setup directly from '{setupExe}'...");

        var psi = new ProcessStartInfo
        {
            FileName = setupExe,
            Arguments = "/auto upgrade /dynamicupdate enable",
            WorkingDirectory = $"{cleanLetter}:{Path.DirectorySeparatorChar}",
            UseShellExecute = true
        };

        Process? started = Process.Start(psi);
        if (started is null)
        {
            throw new InvalidOperationException($"Failed to launch Windows Setup process '{setupExe}'.");
        }

        return Task.CompletedTask;
    }

    private static async Task WriteSwbiDriverReadMeAsync(
        string swbiChooserDriverRoot,
        int storageCount,
        int netCount,
        CancellationToken cancellationToken)
    {
        try
        {
            string readmePath = Path.Combine(swbiChooserDriverRoot, "README-SWBI-DRIVERS.txt");
            string text =
                $"===================================================================\r\n" +
                $"  {SwbiBrand.AsciiTitle} — Exported PC Drivers\r\n" +
                $"===================================================================\r\n" +
                $"OEM Storage Driver Packages : {storageCount}\r\n" +
                $"OEM Network Driver Packages : {netCount}\r\n\r\n" +
                $"Windows Setup automatically scans '$WinPEDriver$' on boot.\r\n" +
                $"If Windows Setup asks to 'Load driver', browse to this folder\r\n" +
                $"('{SwbiBrand.DriverChooserFolderName}\\Storage').\r\n";

            await File.WriteAllTextAsync(readmePath, text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore non-fatal readme write note
        }
    }

    private static long ComputeDirectoryTotalBytes(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return 0;
        }

        long sum = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    sum += new FileInfo(file).Length;
                }
                catch
                {
                    // Skip unreadable file
                }
            }
        }
        catch
        {
            // Ignore traversal warning
        }

        return sum;
    }
}
