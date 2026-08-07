using System.Management;
using System.Runtime.Versioning;

namespace SvchostMonitor;

/// <summary>
/// Maps process IDs to the Windows services they host, via WMI.
/// A single <c>Win32_Service</c> query covers every process in one round trip.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceHostLookup
{
    /// <summary>
    /// Returns PID → hosted service names for all services that currently have a process.
    /// Throws if WMI is unavailable; callers must decide how to fail.
    /// </summary>
    public static IReadOnlyDictionary<int, List<string>> GetServicesByPid()
    {
        var map = new Dictionary<int, List<string>>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId <> 0");
        using var results = searcher.Get();

        foreach (var obj in results)
        {
            using var svc = (ManagementObject)obj;

            var pidRaw = svc["ProcessId"];
            var name = svc["Name"] as string;
            if (pidRaw is null || string.IsNullOrEmpty(name))
                continue;

            var pid = Convert.ToInt32(pidRaw);
            if (pid == 0)
                continue;

            if (!map.TryGetValue(pid, out var names))
                map[pid] = names = new List<string>();

            names.Add(name);
        }

        return map;
    }
}
