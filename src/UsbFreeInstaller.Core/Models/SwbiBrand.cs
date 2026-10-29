namespace UsbFreeInstaller.Core.Models;

/// <summary>
/// Central product and chooser-surface branding constants for SWBI (Self Windows Bootable Installer).
/// Ensures consistent visibility across:
///  1. Partition volume label &amp; File Explorer drive branding (SWBI-SETUP + autorun.inf + swbi.ico)
///  2. Motherboard F12 / F8 / ESC UEFI Boot Device Chooser ("pendrive menu")
///  3. Windows Boot Manager &amp; standalone EFI BCD boot choosers
///  4. Windows Setup partition chooser &amp; Load Driver folder chooser
///  5. Desktop WPF application &amp; post-install cleanup verification
/// </summary>
public static class SwbiBrand
{
    public const string ShortName = "SWBI";
    public const string FullName = "Self Windows Bootable Installer";
    public const string BrandedTitle = "SWBI — Self Windows Bootable Installer";
    public const string AsciiTitle = "SWBI - Self Windows Bootable Installer";
    public const string AppDataFolderName = "SWBI-SelfWindowsBootableInstaller";
    public const string CurrentVersion = "1.0.1";

    /// <summary>
    /// 10-character uppercase FAT32 volume label (strictly within FAT32's 11-character ASCII limit).
    /// Displayed in Windows Setup's "Where do you want to install Windows?" partition chooser as
    /// "Disk X Partition Y: SWBI-SETUP" and in Load Driver browse trees as "SWBI-SETUP (D:)".
    /// </summary>
    public const string PartitionVolumeLabel = "SWBI-SETUP";

    /// <summary>
    /// Legacy v0.1/v0.2 volume label retained for backward-compatible cleanup recognition.
    /// </summary>
    public const string LegacyPartitionVolumeLabel = "WININSTALL";

    /// <summary>
    /// Displayed in Motherboard UEFI F12/F8 Firmware Boot Chooser (pendrive menu),
    /// Windows Boot Manager menu, and standalone EFI BCD store on SWBI-SETUP.
    /// </summary>
    public const string BootChooserEntryName = "SWBI - Self Windows Bootable Installer";

    public const string LegacyBootEntryName = "Windows Setup (USB-Free Local Installer)";

    public const string BcdSdiDeviceName = "SWBI - Self Windows Bootable Installer SDI";
    public const string LegacyBcdSdiDeviceName = "USB-Free Windows Installer SDI";

    /// <summary>
    /// Visible top-level driver folder inside SWBI-SETUP:\ shown in Windows Setup -> Load driver -> Browse
    /// alongside automatic $WinPEDriver$ scanning.
    /// </summary>
    public const string DriverChooserFolderName = "SWBI - Self Windows Bootable Installer Drivers";

    public const string PartitionIconFileName = "swbi.ico";
    public const string PartitionIdentityReadmeFileName = "SWBI - Self Windows Bootable Installer.txt";

    public const string ManifestFileName = ".swbi-installer-manifest.json";
    public const string LegacyManifestFileName = ".usbfree-installer-manifest.json";

    public static bool IsRecognizedPartitionLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label) &&
        (string.Equals(label.Trim(), PartitionVolumeLabel, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(label.Trim(), LegacyPartitionVolumeLabel, StringComparison.OrdinalIgnoreCase));

    public static bool IsRecognizedBootDescription(string? description) =>
        !string.IsNullOrWhiteSpace(description) &&
        (string.Equals(description.Trim(), BootChooserEntryName, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(description.Trim(), LegacyBootEntryName, StringComparison.OrdinalIgnoreCase));

    public static bool IsRecognizedSdiDescription(string? description) =>
        !string.IsNullOrWhiteSpace(description) &&
        (string.Equals(description.Trim(), BcdSdiDeviceName, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(description.Trim(), LegacyBcdSdiDeviceName, StringComparison.OrdinalIgnoreCase));
}
