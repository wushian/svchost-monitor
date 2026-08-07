using System.Diagnostics;

namespace SvchostMonitor;

public static class ProcessMonitor
{
    /// <summary>
    /// Finds all instances of <c>options.ProcessName</c> that exceed the configured
    /// working-set threshold, kills them (unless <c>DryRun</c>), and returns info about each.
    /// Processes hosting a protected Windows service are never killed.
    /// </summary>
    public static IReadOnlyList<KilledProcessInfo> KillOverLimit(
        MonitorOptions options,
        ILogger logger)
    {
        var thresholdBytes = (long)(options.MemThresholdGb * 1024L * 1024L * 1024L);
        var protectedServices = options.ParseProtectedServices();
        var killed = new List<KilledProcessInfo>();

        // Resolve PID → hosted services once per pass. If the guard is enabled and the
        // lookup fails we abort rather than kill blind: an unguarded kill of a process
        // hosting a critical service bugchecks the machine.
        IReadOnlyDictionary<int, List<string>> servicesByPid;
        if (protectedServices.Count == 0)
        {
            servicesByPid = new Dictionary<int, List<string>>();
        }
        else
        {
            try
            {
                servicesByPid = ServiceHostLookup.GetServicesByPid();
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Could not resolve hosted services via WMI — skipping this pass. " +
                    "No process will be killed while the protection guard cannot be evaluated. " +
                    "Set Monitor:ProtectedServices to an empty value to disable the guard.");
                return killed;
            }
        }

        var processes = Process.GetProcessesByName(options.ProcessName);
        logger.LogDebug("Found {Count} {Name} instance(s)", processes.Length, options.ProcessName);

        foreach (var proc in processes)
        {
            try
            {
                proc.Refresh();
                var memBytes = proc.WorkingSet64;
                var memGb = memBytes / (1024.0 * 1024.0 * 1024.0);

                if (memBytes <= thresholdBytes)
                    continue;

                if (servicesByPid.TryGetValue(proc.Id, out var hosted))
                {
                    var blocking = hosted.Where(protectedServices.Contains).ToList();
                    if (blocking.Count > 0)
                    {
                        logger.LogWarning(
                            "{Name} PID={Pid} WorkingSet={MemGb:F2} GB is over the limit but hosts "
                            + "protected service(s) {Services} — NOT killing",
                            options.ProcessName, proc.Id, memGb, string.Join(", ", blocking));
                        continue;
                    }
                }

                if (options.DryRun)
                {
                    logger.LogWarning(
                        "[DryRun] {Name} PID={Pid} WorkingSet={MemGb:F2} GB > {Threshold} GB "
                        + "threshold — would kill (hosts: {Services})",
                        options.ProcessName, proc.Id, memGb, options.MemThresholdGb,
                        hosted is { Count: > 0 } ? string.Join(", ", hosted) : "none");
                    killed.Add(new KilledProcessInfo(proc.Id, memGb));
                    continue;
                }

                logger.LogWarning(
                    "{Name} PID={Pid} WorkingSet={MemGb:F2} GB > {Threshold} GB threshold "
                    + "— killing (hosts: {Services})",
                    options.ProcessName, proc.Id, memGb, options.MemThresholdGb,
                    hosted is { Count: > 0 } ? string.Join(", ", hosted) : "none");

                proc.Kill();

                if (!proc.WaitForExit(3_000))
                {
                    logger.LogError(
                        "{Name} PID={Pid} did not exit within 3 s of Kill() — not reporting it as killed",
                        options.ProcessName, proc.Id);
                    continue;
                }

                logger.LogInformation("{Name} PID={Pid} killed successfully", options.ProcessName, proc.Id);
                killed.Add(new KilledProcessInfo(proc.Id, memGb));
            }
            catch (InvalidOperationException)
            {
                // Process already exited before we could kill it — safe to ignore
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to kill {Name} PID={Pid}", options.ProcessName, proc.Id);
            }
            finally
            {
                proc.Dispose();
            }
        }

        return killed;
    }
}
