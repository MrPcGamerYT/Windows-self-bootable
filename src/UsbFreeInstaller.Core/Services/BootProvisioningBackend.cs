using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Models;
using UsbFreeInstaller.Core.Services.Native;

namespace UsbFreeInstaller.Core.Services;

internal interface IBootProvisioningBackend
{
    bool BackupSystemBcd(string path, ISafeLogger logger);
    ActiveBcdState InspectSystemBcd();
    (string SdiGuid, string LoaderGuid) RegisterRamdiskBcd(string installerDriveLetter, FirmwareMode firmwareMode, ISafeLogger logger);
}

internal sealed class NativeBootProvisioningBackend : IBootProvisioningBackend
{
    public bool BackupSystemBcd(string path, ISafeLogger logger) =>
        NativeUefiAndBcdRegistryEngine.TryExportSystemBcdBackup(path, logger);

    public ActiveBcdState InspectSystemBcd() => NativeUefiAndBcdRegistryEngine.InspectSystemBcdFromRegistry();

    public (string SdiGuid, string LoaderGuid) RegisterRamdiskBcd(string drive, FirmwareMode mode, ISafeLogger logger) =>
        NativeUefiAndBcdRegistryEngine.ProvisionSystemBcdInRegistry(drive, mode, logger);
}
