namespace SvchostMonitor;

public record KilledProcessInfo(int Pid, double MemoryGb);

public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    public string ProcessName { get; set; } = "svchost";
    public double MemThresholdGb { get; set; } = 2.0;
    public int CheckIntervalSeconds { get; set; } = 300;
    public string Remark { get; set; } = string.Empty;

    /// <summary>
    /// When true, over-limit processes are logged and reported but never killed.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Comma-separated Windows service names. A process hosting any of these is never
    /// killed, regardless of memory use. Empty disables the guard entirely.
    /// </summary>
    public string ProtectedServices { get; set; } =
        // Tier 1 — killing these triggers CRITICAL_PROCESS_DIED (bugcheck + reboot)
        "DcomLaunch,RPCSS,LSM,BrokerInfrastructure,PlugPlay,Power,SamSs," +
        // Tier 2 — no bugcheck, but the machine is left badly degraded
        "LanmanServer,LanmanWorkstation,EventLog,Winmgmt,Schedule,CryptSvc," +
        "Dnscache,Dhcp,nsi,NlaSvc,ProfSvc,UserManager,gpsvc,Themes,AudioEndpointBuilder";

    public IReadOnlySet<string> ParseProtectedServices() =>
        ProtectedServices
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
}
