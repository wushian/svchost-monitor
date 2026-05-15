using System.Diagnostics;

namespace SvchostMonitor;

public static class ProcessMonitor
{
    /// <summary>
    /// Finds all instances of <paramref name="processName"/> that exceed
    /// <paramref name="thresholdGb"/> GB of working-set memory, kills them,
    /// and returns info about each killed process.
    /// </summary>
    public static IReadOnlyList<KilledProcessInfo> KillOverLimit(
        string processName,
        double thresholdGb,
        ILogger logger)
    {
        var thresholdBytes = (long)(thresholdGb * 1024L * 1024L * 1024L);
        var killed = new List<KilledProcessInfo>();

        var processes = Process.GetProcessesByName(processName);
        logger.LogDebug("Found {Count} {Name} instance(s)", processes.Length, processName);

        foreach (var proc in processes)
        {
            try
            {
                proc.Refresh();
                var memBytes = proc.WorkingSet64;
                var memGb = memBytes / (1024.0 * 1024.0 * 1024.0);

                if (memBytes <= thresholdBytes)
                    continue;

                logger.LogWarning(
                    "{Name} PID={Pid} WorkingSet={MemGb:F2} GB > {Threshold} GB threshold — killing",
                    processName, proc.Id, memGb, thresholdGb);

                proc.Kill();
                proc.WaitForExit(3_000);

                logger.LogInformation("{Name} PID={Pid} killed successfully", processName, proc.Id);
                killed.Add(new KilledProcessInfo(proc.Id, memGb));
            }
            catch (InvalidOperationException)
            {
                // Process already exited before we could kill it — safe to ignore
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to kill {Name} PID={Pid}", processName, proc.Id);
            }
            finally
            {
                proc.Dispose();
            }
        }

        return killed;
    }
}
