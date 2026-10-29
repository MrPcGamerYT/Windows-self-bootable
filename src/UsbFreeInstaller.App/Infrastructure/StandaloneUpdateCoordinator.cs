using System.Diagnostics;
using System.IO;
using UsbFreeInstaller.Core.Services.Updates;

namespace UsbFreeInstaller.App.Infrastructure;

/// <summary>The extracted app starts a second copy as a short-lived helper, then exits.</summary>
internal static class StandaloneUpdateCoordinator
{
    internal const string ApplySwitch = "--swbi-apply-update";

    internal static void StartHelper(UpdateApplyPlan plan)
    {
        string helper = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the SWBI update helper.");
        using Process parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(ApplySwitch);
        start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(plan.TargetPath);
        start.ArgumentList.Add(plan.StagedPath);
        start.ArgumentList.Add(plan.BackupPath);
        start.ArgumentList.Add(plan.OldSha256);
        start.ArgumentList.Add(plan.NewSha256);
        start.ArgumentList.Add(plan.Flavor.ToString());
        using Process? child = Process.Start(start);
        if (child is null) throw new InvalidOperationException("Windows could not start the update helper; the original EXE is unchanged.");
    }

    internal static async Task ApplyFromArgumentsAsync(string[] args)
    {
        if ((args.Length != 9 && args.Length != 12) || args[0] != ApplySwitch ||
            !int.TryParse(args[1], out int parentId) || parentId <= 0 || parentId == Environment.ProcessId ||
            !long.TryParse(args[2], out long parentStartTicks) ||
            !Enum.TryParse(args[8], out StandaloneFlavor flavor) || !Enum.IsDefined(flavor))
            throw new InvalidOperationException("Invalid update helper arguments.");

        Process? parent = null;
        try { parent = Process.GetProcessById(parentId); }
        catch (ArgumentException) { /* The original app has already exited. */ }
        if (parent is not null)
        {
            using (parent)
            {
                if (parent.StartTime.ToUniversalTime().Ticks != parentStartTicks)
                    throw new InvalidOperationException("The original SWBI process identity changed; the update was not applied.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await parent.WaitForExitAsync(timeout.Token);
            }
        }

        if (args.Length == 12 && (args[9] != "--smoke-test" || args[10] != "--smoke-report" ||
            !Path.IsPathFullyQualified(args[11]) || !args[11].EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Invalid update smoke-test arguments.");
        var plan = new UpdateApplyPlan(args[3], args[4], args[5], args[6], args[7], flavor);
        await UpdateFileTransaction.ApplyAsync(plan, path =>
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
            if (args.Length == 12)
            {
                start.ArgumentList.Add(args[9]);
                start.ArgumentList.Add(args[10]);
                start.ArgumentList.Add(args[11]);
            }
            using Process? launched = Process.Start(start);
            return launched is not null;
        });
    }
}
