using UsbFreeInstaller.Core.Abstractions;
using UsbFreeInstaller.Core.Services;
using Xunit;

namespace UsbFreeInstaller.Core.Tests;

public sealed class SafeRedactingLoggerTests
{
    [Fact]
    public void RedactSecrets_Strips48DigitBitLockerRecoveryKeyAndProductKey()
    {
        const string input =
            "Recovery password: 123456-654321-111222-333444-555666-777888-999000-424242 and key VK7JG-NPHTM-C97JM-9MPGT-3V66T";

        string redacted = SafeRedactingLogger.RedactSecrets(input);

        Assert.DoesNotContain("123456-654321", redacted);
        Assert.DoesNotContain("VK7JG-NPHTM", redacted);
        Assert.Contains("[REDACTED-BITLOCKER-RECOVERY-KEY]", redacted);
        Assert.Contains("[REDACTED-PRODUCT-KEY]", redacted);
    }

    [Fact]
    public void Log_NeverPersistsSecretsInMemorySnapshot()
    {
        var logger = new SafeRedactingLogger();
        logger.Log(SafeLogLevel.Info, "BitLocker", "User pasted 111111 222222 333333 444444 555555 666666 777777 888888 accidentally");

        IReadOnlyList<SafeLogEntry> entries = logger.GetEntriesSnapshot();
        Assert.Single(entries);
        Assert.DoesNotContain("111111 222222", entries[0].Message);
        Assert.Contains("[REDACTED-BITLOCKER-RECOVERY-KEY]", entries[0].Message);
    }
    [Fact]
    public void Log_WhenObserverThrows_DoesNotInterruptOperationOrOtherObservers()
    {
        var logger = new SafeRedactingLogger();
        bool secondObserverCalled = false;
        logger.EntryWritten += (_, _) => throw new InvalidOperationException("UI listener failure");
        logger.EntryWritten += (_, _) => secondObserverCalled = true;
        logger.Log(SafeLogLevel.Info, "Boot", "Entry recorded");
        Assert.True(secondObserverCalled);
        Assert.Single(logger.GetEntriesSnapshot());
    }
}
