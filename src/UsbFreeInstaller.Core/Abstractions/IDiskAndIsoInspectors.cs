using UsbFreeInstaller.Core.Models;

namespace UsbFreeInstaller.Core.Abstractions;

public interface IDiskInspector
{
    Task<SystemEnvironmentSnapshot> InspectEnvironmentAsync(
        CancellationToken cancellationToken = default);
}

public interface IIsoInspector
{
    Task<IsoInspectionResult> InspectIsoAsync(
        string isoPath,
        bool computeSha256,
        IProgress<int>? hashProgress = null,
        CancellationToken cancellationToken = default);
}
