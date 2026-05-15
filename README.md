# Svchost Monitor

A .NET 8 Windows Service that monitors a target process and automatically kills any instance exceeding a configured memory threshold, then sends a Telegram alert and writes to a rolling log file.

## Features

- Monitors any Windows process by name (default: `svchost.exe`)
- Configurable memory threshold (default: 2 GB)
- Configurable check interval (default: every 5 minutes)
- Telegram Bot notification on kill, including host name and IP for easy source identification
- Daily rolling log file (retained 30 days)
- Settings managed via `sys.ini` with CLI argument override support
- Runs as a Windows Service under `LocalSystem` account

## Project Structure

```
├── Program.cs              # Host setup, Serilog, sys.ini config chain
├── Worker.cs               # BackgroundService monitoring loop
├── ProcessMonitor.cs       # Enumerate and kill over-limit processes
├── TelegramNotifier.cs     # Telegram Bot API notification
├── Models.cs               # Options / record definitions
├── SvchostMonitor.csproj   # .NET 8 Worker Service project
├── sys.ini                 # Configuration file
├── install_service.bat     # Install as Windows Service (run as Admin)
└── uninstall_service.bat   # Remove Windows Service (run as Admin)
```

## Requirements

- Windows 10 / 11 or Windows Server 2019+
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or publish as self-contained)
- Administrator privileges for service installation

## Configuration (`sys.ini`)

```ini
[Monitor]
ProcessName=svchost
MemThresholdGb=2.0
CheckIntervalSeconds=300

[Telegram]
BotToken=YOUR_BOT_TOKEN_HERE
ChatId=YOUR_CHAT_ID_HERE
```

### Telegram Setup

1. Create a bot via [@BotFather](https://t.me/BotFather) and copy the **Bot Token**
2. Get your **Chat ID** via [@userinfobot](https://t.me/userinfobot)
3. Fill in `BotToken` and `ChatId` in `sys.ini`

## Build

```bash
dotnet publish SvchostMonitor.csproj -c Release -r win-x64 --self-contained true -o publish
```

## Installation

1. Copy `SvchostMonitor.exe` and `sys.ini` to the same folder
2. Edit `sys.ini` — set `BotToken` and `ChatId`
3. Run `install_service.bat` **as Administrator**

The service is registered as `ProcessMemMonitor`, set to auto-start under `LocalSystem`.

```
Services name : ProcessMemMonitor
Display name  : Process Memory Monitor
Start type    : Automatic
Log on as     : Local System
```

## Uninstall

Run `uninstall_service.bat` as Administrator.

## CLI Argument Override

Command-line arguments take precedence over `sys.ini`, useful for testing without editing the file:

```bat
SvchostMonitor.exe --Monitor:ProcessName notepad --Monitor:MemThresholdGb 0.5 --Monitor:CheckIntervalSeconds 60
```

To change the service's startup arguments after installation:

```bat
sc config ProcessMemMonitor binPath= "\"C:\path\to\SvchostMonitor.exe\" --Monitor:MemThresholdGb 3.0"
```

## Configuration Priority (low → high)

```
sys.ini  <  Environment Variables  <  CLI Arguments
```

## Log File

Logs are written to `logs\monitor-YYYYMMDD.log` in the same directory as the executable. Files rotate daily and are retained for 30 days.

```
2026-05-15 10:00:00 [WRN] svchost.exe PID=1234 WorkingSet=2.31 GB > 2.0 GB threshold — killing
2026-05-15 10:00:00 [INF] svchost.exe PID=1234 killed successfully
2026-05-15 10:00:01 [INF] Telegram notification sent (1 process(es) killed)
```

## Telegram Alert Example

```
🚨 Process Memory Alert
🖥 Host: DESKTOP-ABC123
📡 IP:   192.168.1.50
🕐 Time: 2026-05-15 10:00:00
📋 Process: svchost

• PID 1234 | 2.31 GB → KILLED
```

### Host IP Detection

The IP address shown in the alert is the machine's outbound network interface IP, resolved by:

1. Opening a UDP socket toward `8.8.8.8:80` (no packet is actually sent) to determine which local interface the OS would use for external traffic
2. Fallback: first non-loopback IPv4 address returned by `Dns.GetHostAddresses()`
3. Final fallback: displays `unknown`

This ensures the correct IP is shown on multi-NIC machines (e.g., a server with both internal and external interfaces).
