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
            "Worker started — Process={Process} Threshold={Threshold} GB Interval={Interval} s",
            _monitor.ProcessName, _monitor.MemThresholdGb, _monitor.CheckIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var killed = ProcessMonitor.KillOverLimit(
                    _monitor.ProcessName,
                    _monitor.MemThresholdGb,
                    _logger);

                if (killed.Count > 0)
                {
                    await TelegramNotifier.SendAsync(
                        _telegram, _monitor.ProcessName, _monitor.Remark, killed, _logger, stoppingToken);
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
