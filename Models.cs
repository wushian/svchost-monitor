namespace SvchostMonitor;

public record KilledProcessInfo(int Pid, double MemoryGb);

public sealed class MonitorOptions
{
    public const string SectionName = "Monitor";

    public string ProcessName { get; set; } = "svchost";
    public double MemThresholdGb { get; set; } = 2.0;
    public int CheckIntervalSeconds { get; set; } = 300;
}

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
}
