using Microsoft.Extensions.Options;

namespace SvchostMonitor;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly MonitorOptions  _monitor;
    private readonly TelegramOptions _telegram;

    public Worker(
        ILogger<Worker> logger,
        IOptions<MonitorOptions>  monitor,
        IOptions<TelegramOptions> telegram)
    {
        _logger   = logger;
        _monitor  = monitor.Value;
        _telegram = telegram.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker started — Process={Process} Threshold={Threshold} GB Interval={Interval} s "
            + "DryRun={DryRun} ProtectedServices={Protected}",
            _monitor.ProcessName, _monitor.MemThresholdGb, _monitor.CheckIntervalSeconds,
            _monitor.DryRun,
            _monitor.ParseProtectedServices() is { Count: > 0 } p ? string.Join(", ", p) : "(none — guard disabled)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var killed = ProcessMonitor.KillOverLimit(_monitor, _logger);

                if (killed.Count > 0)
                {
                    await TelegramNotifier.SendAsync(
                        _telegram, _monitor, killed, _logger, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during monitoring cycle");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_monitor.CheckIntervalSeconds),
                stoppingToken);
        }

        _logger.LogInformation("Worker stopped");
    }
}
