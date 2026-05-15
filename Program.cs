using Microsoft.Extensions.Configuration;
using Serilog;
using SvchostMonitor;

var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "monitor-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// --test mode: send a test Telegram notification and exit
if (args.Length > 0 && args[0].Equals("--test", StringComparison.OrdinalIgnoreCase))
{
    var cfg = new ConfigurationBuilder()
        .AddIniFile(Path.Combine(AppContext.BaseDirectory, "sys.ini"), optional: false)
        .AddEnvironmentVariables()
        .AddCommandLine(args.Skip(1).ToArray())
        .Build();

    var monitor  = cfg.GetSection(MonitorOptions.SectionName).Get<MonitorOptions>()  ?? new MonitorOptions();
    var telegram = cfg.GetSection(TelegramOptions.SectionName).Get<TelegramOptions>() ?? new TelegramOptions();

    var dummy = new List<KilledProcessInfo> { new(9999, 2.55) };

    using var loggerFactory = LoggerFactory.Create(b => b.AddSerilog());
    var testLogger = loggerFactory.CreateLogger("Test");

    Log.Information("Sending test Telegram notification …");
    await TelegramNotifier.SendAsync(telegram, monitor.ProcessName, monitor.Remark, dummy, testLogger);
    Log.CloseAndFlush();
    return 0;
}

try
{
    Log.Information("========== Process Memory Monitor starting ==========");

    var host = Host.CreateDefaultBuilder(args)
        .UseWindowsService(o => o.ServiceName = "ProcessMemMonitor")
        .UseSerilog()
        .ConfigureAppConfiguration((_, config) =>
        {
            // Priority chain (low → high): sys.ini < env vars < CLI args
            config.Sources.Clear();
            config.AddIniFile(
                Path.Combine(AppContext.BaseDirectory, "sys.ini"),
                optional: false,
                reloadOnChange: true);
            config.AddEnvironmentVariables();
            config.AddCommandLine(args);
        })
        .ConfigureServices((ctx, services) =>
        {
            services.Configure<MonitorOptions>(
                ctx.Configuration.GetSection(MonitorOptions.SectionName));
            services.Configure<TelegramOptions>(
                ctx.Configuration.GetSection(TelegramOptions.SectionName));
            services.AddHostedService<Worker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
