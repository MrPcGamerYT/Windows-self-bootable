using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

internal interface IBootProvisioningBackend
{
    string RegisterUefi(string installerDriveLetter, ISafeLogger logger);
    bool BackupSystemBcd(string path, ISafeLogger logger);
    ActiveBcdState InspectSystemBcd();
    (string SdiGuid, string LoaderGuid) RegisterLegacyBcd(string installerDriveLetter, ISafeLogger logger);
}

internal sealed class NativeBootProvisioningBackend : IBootProvisioningBackend
{
    public string RegisterUefi(string drive, ISafeLogger logger) =>
        NativeUefiAndBcdRegistryEngine.ProvisionUefiNvramBootEntry(drive, logger);

    public bool BackupSystemBcd(string path, ISafeLogger logger) =>
        NativeUefiAndBcdRegistryEngine.TryExportSystemBcdBackup(path, logger);

    public ActiveBcdState InspectSystemBcd() => NativeUefiAndBcdRegistryEngine.InspectSystemBcdFromRegistry();

    public (string SdiGuid, string LoaderGuid) RegisterLegacyBcd(string drive, ISafeLogger logger) =>
        NativeUefiAndBcdRegistryEngine.ProvisionSystemBcdInRegistry(drive, FirmwareMode.LegacyBios, logger);
}
