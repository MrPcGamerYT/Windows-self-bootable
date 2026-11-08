using UsbFreeInstaller.Core.Services.Native;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class BootWimReplacementTests
{
    [Theory]
    [InlineData(true)]  // FAT32-style same-volume rename fallback
    [InlineData(false)] // ReplaceFile when the filesystem supports it
    public void ReplacesOnlyAfterVerification_AndPreservesOriginal(bool renameFallback)
    {
        WithFiles((dir, original, rebuilt) =>
        {
            File.WriteAllText(original, "original ISO image");
            File.WriteAllText(rebuilt, "verified new image");
            string backup = BootWimReplacement.Install(rebuilt, original,
                p => File.ReadAllText(p) == "verified new image", renameFallback);

            Assert.Equal("verified new image", File.ReadAllText(original));
            Assert.Equal("original ISO image", File.ReadAllText(backup));
            Assert.False(File.Exists(original + ".new"));
        });
    }

    [Fact]
    public void SecondRebuild_KeepsOriginalIsoBackup_NotARebuildOfARebuild()
    {
        WithFiles((dir, original, rebuilt) =>
        {
            File.WriteAllText(original, "original");
            File.WriteAllText(rebuilt, "labconfig");
            BootWimReplacement.Install(rebuilt, original, p => File.ReadAllText(p) == "labconfig", true);
            File.WriteAllText(rebuilt, "drivers and labconfig");
            BootWimReplacement.Install(rebuilt, original, p => File.ReadAllText(p) == "drivers and labconfig", true);

            Assert.Equal("drivers and labconfig", File.ReadAllText(original));
            Assert.Equal("original", File.ReadAllText(original + ".swbi-backup"));
            Assert.False(File.Exists(original + ".swbi-previous"));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedPostSwapVerification_RestoresPreviousBootImage(bool renameFallback)
    {
        WithFiles((dir, original, rebuilt) =>
        {
            File.WriteAllText(original, "original");
            File.WriteAllText(rebuilt, "bad boot image");
            int verifications = 0;
            Assert.Throws<InvalidDataException>(() => BootWimReplacement.Install(rebuilt, original,
                p => ++verifications == 1, renameFallback));

            Assert.Equal("original", File.ReadAllText(original));
            Assert.False(File.Exists(original + ".new"));
        });
    }

    [Fact]
    public void MissingRollbackAfterSwap_IsFatal_NotAnOptionalDriverWarning()
    {
        WithFiles((dir, original, rebuilt) =>
        {
            File.WriteAllText(original, "original");
            File.WriteAllText(rebuilt, "new");
            int verifications = 0;
            Assert.Throws<BootWimRecoveryException>(() => BootWimReplacement.Install(rebuilt, original, p =>
            {
                if (++verifications == 1) return true;
                File.Delete(original + ".swbi-backup"); // simulated external deletion during verification
                return false;
            }, true));
        });
    }

    [Fact]
    public void BadStagedImage_NeverTouchesTheBootFile()
    {
        WithFiles((dir, original, rebuilt) =>
        {
            File.WriteAllText(original, "original");
            File.WriteAllText(rebuilt, "invalid");
            Assert.Throws<InvalidDataException>(() => BootWimReplacement.Install(rebuilt, original, _ => false, true));
            Assert.Equal("original", File.ReadAllText(original));
            Assert.False(File.Exists(original + ".new"));
            Assert.False(File.Exists(original + ".swbi-backup"));
        });
    }

    private static void WithFiles(Action<string, string, string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"swbi-wim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try { test(dir, Path.Combine(dir, "boot.wim"), Path.Combine(dir, "rebuilt.wim")); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
