namespace UsbFreeInstaller.Core.Services.Native;

// The filesystem must be committed and checked before partmgr can release its tail.
// Keeping this sequence separate from the Win32 calls also lets us test every failure boundary.
internal interface IOnlineShrinkOperations
{
    void Prepare();
    void Evacuate();
    void Commit();
    void VerifyFileSystemFitsTarget();
    void ResizePartition();
    void VerifyResult();
    void Abort();
    bool TryRestorePartitionLength();
}

internal static class OnlineShrinkTransaction
{
    internal static void Execute(IOnlineShrinkOperations operations)
    {
        bool prepareAttempted = false;
        bool committed = false;
        bool resizeAttempted = false;

        try
        {
            prepareAttempted = true;
            operations.Prepare();
            operations.Evacuate();
            operations.Commit();
            committed = true;
            operations.VerifyFileSystemFitsTarget();
            resizeAttempted = true;
            operations.ResizePartition();
            operations.VerifyResult();
        }
        catch (Exception error)
        {
            if (!committed)
            {
                string abortDetail = string.Empty;
                if (prepareAttempted)
                {
                    try
                    {
                        operations.Abort();
                    }
                    catch (Exception abortError)
                    {
                        abortDetail = $" The NTFS abort also failed: {abortError.Message}.";
                    }
                }

                throw new InvalidOperationException(
                    "NTFS shrink failed before the partition was resized; no installer partition was created." +
                    $" The filesystem state may be uncertain; check the volume and your backup before retrying.{abortDetail} Details: {error.Message}", error);
            }

            string partitionDetail = "The partition resize was not attempted.";
            if (resizeAttempted)
            {
                try
                {
                    partitionDetail = operations.TryRestorePartitionLength()
                        ? "The original partition length was verified or restored."
                        : "The original partition length could not be verified or restored.";
                }
                catch (Exception restoreError)
                {
                    partitionDetail = $"Restoring the original partition length failed: {restoreError.Message}.";
                }
            }

            throw new InvalidOperationException(
                "NTFS shrink stopped after the filesystem commit; no installer partition was created. " +
                $"{partitionDetail} The filesystem may remain smaller and recovery is not automatic. " +
                $"Protect your files and inspect the disk before retrying. Details: {error.Message}", error);
        }
    }
}
