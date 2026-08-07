# Svchost Monitor

A .NET 8 Windows Service that polls a target process by name, kills every instance whose
**working set** exceeds a configured threshold, then sends a Telegram alert and writes to a
daily rolling log file.

> ⚠ **Read [Known Limitations](#known-limitations) before pointing this at `svchost`.**
> Killing the wrong `svchost` can bugcheck the machine. A protected-service guard is enabled
> by default, but the list is a starting point — run with `DryRun=true` first.

## What it actually does

Every `CheckIntervalSeconds` the worker runs one pass:

1. Query WMI once for a PID → hosted-service map (skipped if the guard is disabled)
2. `Process.GetProcessesByName(ProcessName)` — name only, no `.exe`, no path or user filter
3. For each instance, compare `WorkingSet64` against `MemThresholdGb`
4. Over the limit, but hosting a service in `ProtectedServices` → log a warning and **skip**
5. Otherwise → `Kill()`, then `WaitForExit(3000)`; a process that does not exit within 3 s is
   logged as an error and *not* reported as killed
6. If the pass killed ≥ 1 process → one Telegram message listing all of them
7. Sleep, repeat

With `DryRun=true`, step 5 is skipped entirely: over-limit processes are logged and still
reported via Telegram (tagged `[DryRun]`), but nothing is killed.

If a pass throws, the exception is logged and the loop continues. A pass that kills nothing
sends nothing. **If the WMI lookup fails while the guard is enabled, the whole pass is
skipped** — the tool refuses to kill blind rather than risk killing a critical service.

### Data flow

```
                    ┌──────────────────────────────────────────┐
   sys.ini ──┐      │  Worker.ExecuteAsync (BackgroundService)  │
   env vars ─┼─▶ IOptions<T> ──▶ │                              │
   CLI args ─┘   (read once,     │   loop every CheckInterval   │
                  at startup)    └──────────────┬───────────────┘
                                                │
                                                ▼
                              ProcessMonitor.KillOverLimit()
                              GetProcessesByName → WorkingSet64
                                                │
                                    over threshold? ──no──▶ (sleep)
                                                │ yes
                                          proc.Kill()
                                                │
                                    List<KilledProcessInfo>
                                                │
                              ┌─────────────────┴─────────────────┐
                              ▼                                   ▼
                    TelegramNotifier.SendAsync            Serilog console + file
                    POST api.telegram.org                 logs\monitor-YYYYMMDD.log
                    (fire once, no retry)                 (rolls daily, keeps 30)
```

## Project Structure

| File | Responsibility |
|------|----------------|
| `Program.cs` | Serilog bootstrap, `--test` short-circuit path, generic host + `UseWindowsService`, config chain |
| `Worker.cs` | `BackgroundService` polling loop; catches per-cycle exceptions, calls monitor + notifier |
| `ProcessMonitor.cs` | Enumerates processes by name, applies the protected-service guard, compares `WorkingSet64`, kills, returns what was killed |
| `ServiceHostLookup.cs` | One WMI `Win32_Service` query → PID-to-hosted-service-names map |
| `TelegramNotifier.cs` | Builds the MarkdownV2 alert with escaping, resolves host IP, POSTs to the Telegram Bot API, falls back to plain text on 400 |
| `Models.cs` | `MonitorOptions`, `TelegramOptions`, `KilledProcessInfo` record |
| `sys.ini.example` | Tracked configuration template |
| `sys.ini` | Real local configuration — **gitignored**; copied to output only if it exists |
| `install_service.bat` | Admin-only: `sc create` the service as `LocalSystem`, auto-start, then `net start` |
| `uninstall_service.bat` | Admin-only: `net stop` + `sc delete` |
| `SvchostMonitor.csproj` | `net8.0-windows`, Worker SDK, nullable + implicit usings enabled |

## Requirements

- Windows 10 / 11 or Windows Server 2019+
- **Build:** .NET 8 SDK or newer — verified building `net8.0-windows` with only the
  **.NET 9.0.309 SDK** installed (no .NET 8 SDK on the machine)
- **Run:** .NET 8 Desktop/Runtime on the target, *or* publish self-contained (see below)
- Administrator privileges to install the service
- The WMI service (`Winmgmt`) must be running — the protected-service guard queries
  `Win32_Service` through it

## Configuration

`sys.ini` is **not tracked in git** — it holds real credentials. The tracked template is
`sys.ini.example`; copy it to `sys.ini` and fill it in, or supply secrets via environment
variables (see below).

`sys.ini` must sit **next to the executable** — it is resolved from `AppContext.BaseDirectory`,
not the working directory. It is loaded with `optional: false`, so **a missing `sys.ini` throws
at startup and the service will not start.**

```ini
[Monitor]
; Process name WITHOUT .exe
ProcessName=svchost
; Working-set ceiling in GB; strictly greater than this is killed
MemThresholdGb=2.0
; Seconds between passes
CheckIntervalSeconds=300
; Free-text label shown in the Telegram alert; omitted from the message when blank
Remark=
; true = log and notify what would be killed, but kill nothing
DryRun=false
; Comma-separated service names; a process hosting any of them is never killed.
; Empty disables the guard. See sys.ini.example for the full default list.
ProtectedServices=DcomLaunch,RPCSS,LSM,...

[Telegram]
BotToken=YOUR_BOT_TOKEN_HERE
ChatId=YOUR_CHAT_ID_HERE
```

### Keeping secrets out of the file

Environment variables outrank `sys.ini`, so the token never has to be written to disk:

```bat
setx Telegram__BotToken "123456:ABC..." /M
setx Telegram__ChatId   "-1001234567890" /M
```

(Double underscore is the .NET configuration separator; `/M` sets it machine-wide so the
`LocalSystem` service account sees it.)

### The protected-service guard

`ProtectedServices` is matched case-insensitively against the services each PID hosts, resolved
through WMI. The default list has two tiers:

- **Bugcheck tier** — `DcomLaunch`, `RPCSS`, `LSM`, `BrokerInfrastructure`, `PlugPlay`, `Power`,
  `SamSs`. Killing these is a critical process death: instant bluescreen and reboot.
- **Degradation tier** — `LanmanServer`, `LanmanWorkstation`, `EventLog`, `Winmgmt`, `Schedule`,
  `CryptSvc`, `Dnscache`, `Dhcp`, `nsi`, `NlaSvc`, `ProfSvc`, `UserManager`, `gpsvc`, `Themes`,
  `AudioEndpointBuilder`. No bluescreen, but logon, networking, scheduling, or event logging
  break.

**This list is a starting point, not a complete one.** Which services share an `svchost` varies
by machine and Windows build. Run once with `DryRun=true` and read the `would kill (hosts: …)`
lines before trusting it.

`MemThresholdGb` is compared against **`Process.WorkingSet64`** — resident physical memory,
including pages shared with other processes. It is *not* private bytes and *not* commit size,
so it reads lower than the "Commit size" column and roughly matches Task Manager's "Memory
(active private working set)" only for processes with little sharing.

### Configuration priority (low → high)

```
sys.ini  <  Environment Variables  <  CLI Arguments
```

Values are bound **once at startup** into `IOptions<T>`. Although `sys.ini` is registered with
`reloadOnChange: true`, the worker captures `.Value` in its constructor, so **editing `sys.ini`
while the service runs has no effect** — restart it.

### Telegram setup

1. Create a bot via [@BotFather](https://t.me/BotFather) and copy the **Bot Token**
2. Get the **Chat ID** via [@userinfobot](https://t.me/userinfobot) (group chat IDs are negative)
3. Fill in `BotToken` and `ChatId`

If either is blank, the notifier logs a warning and returns — the kill still happens, silently.

## Build

Self-contained, **single file** — this is what makes the two-file deployment below true:

```bat
dotnet publish SvchostMonitor.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Verified output: `publish\` contains `SvchostMonitor.exe` (~67 MB), `SvchostMonitor.pdb`,
and `sys.ini`.

Without `-p:PublishSingleFile=true`, the same command emits **225 files / ~73 MB** — the exe
alone will not run, you must copy the whole folder.

Framework-dependent build (requires .NET 8 runtime on the target):

```bat
dotnet publish SvchostMonitor.csproj -c Release -o publish
```

## Installation

### Step 1 — Deploy

Copy to any folder on the target machine, e.g. `C:\Tools\SvchostMonitor\`:

| File | Source | Notes |
|------|--------|-------|
| `SvchostMonitor.exe` | `publish\` | Single-file build; otherwise copy the entire `publish\` folder |
| `sys.ini` | copy from `sys.ini.example` | Must be beside the exe; not in git |
| `install_service.bat` | repo root | **Not produced by `dotnet publish`** — copy from the repo |
| `uninstall_service.bat` | repo root | Same |

Pick a folder the `LocalSystem` account can write to — the service creates a `logs\`
subdirectory next to the exe. Serilog's file sink fails silently if it cannot, so under
`C:\Program Files\` you get a running service with no log file.

### Step 2 — Configure `sys.ini`

Copy `sys.ini.example` to `sys.ini` and fill in `BotToken`, `ChatId`, and the `Monitor` values
for this machine. **Set `DryRun=true` for the first run.**

### Step 3 — Test Telegram (recommended)

```bat
SvchostMonitor.exe --test
```

This sends **one dummy alert** and exits — it does not enumerate or kill anything. The payload
is hard-coded as `PID 9999 | 2.55 GB`, and the success line always says
`1 process(es) killed`; that is the fixed test payload, not a real kill.

```
11:08:44 [INF] Sending test Telegram notification …
11:08:46 [INF] Telegram notification sent (1 process(es) killed)
```

### Step 4 — Install as a Windows Service

Run `install_service.bat` **as Administrator**. It verifies the exe is present, deletes any
existing `ProcessMemMonitor` service, creates a new one, and starts it.

```
Service name : ProcessMemMonitor
Display name : Process Memory Monitor
Start type   : Automatic
Log on as    : LocalSystem
```

`LocalSystem` is required to kill `svchost.exe` instances owned by `SYSTEM`.

### Step 5 — Verify

```bat
sc query ProcessMemMonitor
type logs\monitor-%date:~0,4%%date:~5,2%%date:~8,2%.log
```

Expected first lines:

```
2026-08-07 11:01:11 [INF] ========== Process Memory Monitor starting ==========
2026-08-07 11:01:11 [INF] Worker started — Process=notepad Threshold=9999 GB Interval=2 s
2026-08-07 11:01:11 [INF] Application started. Press Ctrl+C to shut down.
```

## Uninstall

```bat
uninstall_service.bat
```

Stops and removes the service. The executable, `sys.ini`, and `logs\` are left in place.

## Reconfigure

Edit `sys.ini`, then restart — config is not hot-reloaded:

```bat
net stop ProcessMemMonitor & net start ProcessMemMonitor
```

To override via startup arguments instead:

```bat
sc config ProcessMemMonitor binPath= "\"C:\Tools\SvchostMonitor\SvchostMonitor.exe\" --Monitor:MemThresholdGb 3.0"
net stop ProcessMemMonitor & net start ProcessMemMonitor
```

## CLI argument override

```bat
SvchostMonitor.exe --Monitor:ProcessName notepad --Monitor:MemThresholdGb 0.5 --Monitor:CheckIntervalSeconds 60
```

Running the exe directly (not as a service) runs the same loop in the console until Ctrl+C.

Dry run against the real target — safe, kills nothing, prints which services each candidate
hosts:

```bat
SvchostMonitor.exe --Monitor:MemThresholdGb 0.001 --Monitor:DryRun true
```

## Log file

`logs\monitor-YYYYMMDD.log`, beside the executable. Rolls daily, keeps 30 files. Minimum level
is `Information`, so the per-pass `Found N instance(s)` line (logged at `Debug`) never appears.

```
2026-05-15 10:00:00 [WRN] svchost PID=1408 WorkingSet=2.50 GB is over the limit but hosts protected service(s) DcomLaunch, PlugPlay — NOT killing
2026-05-15 10:00:00 [WRN] svchost PID=1234 WorkingSet=2.31 GB > 2 GB threshold — killing (hosts: DiagTrack)
2026-05-15 10:00:00 [INF] svchost PID=1234 killed successfully
2026-05-15 10:00:01 [INF] Telegram notification sent (1 process(es) KILLED)
```

## Telegram alert example

```
🚨 Process Memory Alert
🖥 Host: DESKTOP-ABC123
📡 IP:   192.168.1.50
🕐 Time: 2026-05-15 10:00:00 +08:00
📋 Process: svchost
📝 Remark: 產線A機台

• PID 1234 | 2.31 GB → KILLED
```

With `DryRun=true` the title reads `Process Memory Alert [DryRun]` and each line ends
`→ WOULD KILL (DryRun)`.

The message is sent as `MarkdownV2` with every interpolated value escaped, so a `Remark`
containing `_ * [ ] ( ) \` or a Windows path is safe. If Telegram still rejects the formatting
with a 400, the notifier immediately resends the same content as unformatted plain text — a
formatting problem must never cost you the alert.

### Host IP detection

1. Open a UDP socket toward `8.8.8.8:80` (no packet is sent) and read the local endpoint the OS
   picked — this yields the interface used for outbound traffic on multi-NIC machines
2. Fallback: first non-loopback IPv4 from `Dns.GetHostAddresses()`
3. Final fallback: `unknown`

Note this reports the **outbound-route** IP, which on a machine behind a VPN is the VPN
adapter's address, not the LAN address.

## Known Limitations

- **The protected-service list is not exhaustive.** It covers the services that bugcheck or
  cripple a standard Windows install, but service-to-`svchost` grouping varies by machine and
  build, and third-party services are not covered at all. `DryRun=true` is the only way to see
  the real grouping on a given host.
- **Processes match by name only.** Any process named `svchost` anywhere on disk — including
  one dropped in a user-writable directory — is a candidate. There is no path or signature
  check.
- **A process hosting no service at all is never protected.** The guard only knows about
  services; a non-service process that happens to share the name is killed on sight.
- **Config is not hot-reloaded** despite `reloadOnChange: true`; `IOptions<T>` binds once at
  startup. Restart the service after editing `sys.ini`.
- **No retry on the Telegram POST.** A transient failure (timeout, 429, 5xx) loses that alert —
  logged, never resent. The plain-text fallback covers *formatting* rejections (400) only.
- **A misconfigured or empty `BotToken`/`ChatId` fails silently** — processes are still killed,
  only a warning is logged.
- **The service has no failure-recovery configuration.** `install_service.bat` does not call
  `sc failure`, so if the host process dies it stays down until reboot.
- **WMI is queried every pass.** On a machine with ~100 `svchost` instances this is one extra
  round trip per `CheckIntervalSeconds` — negligible at the 300 s default, wasteful if you set
  the interval to a few seconds.
- **No tests.** The repository contains no test project.
- **Service installation and the real kill path are unverified here.** The published exe was
  run in console mode against live `svchost` with `DryRun=true` (guard confirmed working) and
  with a non-killing threshold. `sc create` installation, an actual `Kill()`, and a live
  Telegram delivery have not been executed.
