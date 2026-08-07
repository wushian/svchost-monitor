using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SvchostMonitor;

public static class TelegramNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Characters Telegram requires escaping in MarkdownV2 body text. The backslash is
    /// included deliberately: it is MarkdownV2's own escape character, so an unescaped
    /// one (e.g. a Windows path in Remark) produces an invalid sequence and a 400.
    /// </summary>
    private const string ReservedV2 = "\\_*[]()~`>#+-=|{}.!";

    private static string EscMd(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (ReservedV2.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Inside a MarkdownV2 code entity only backtick and backslash are special.</summary>
    private static string EscCode(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (c is '`' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            // Fallback: first non-loopback IPv4
            foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
                if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    return addr.ToString();

            return "unknown";
        }
    }

    public static async Task SendAsync(
        TelegramOptions opts,
        MonitorOptions monitor,
        IReadOnlyList<KilledProcessInfo> killed,
        ILogger logger,
        CancellationToken ct = default)
    {
        // Treat the shipped placeholders as "unset": they are non-empty, so without this
        // check an unconfigured install silently POSTs to a bogus URL and logs a 404 every cycle.
        static bool Unset(string v) =>
            string.IsNullOrWhiteSpace(v) || v.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);

        if (Unset(opts.BotToken) || Unset(opts.ChatId))
        {
            logger.LogWarning(
                "Telegram not configured (BotToken/ChatId missing or still the sys.ini.example "
                + "placeholder) — skipping notification. {Count} process(es) went unreported.",
                killed.Count);
            return;
        }

        var hostName = Environment.MachineName;
        var hostIp   = GetLocalIp();
        var ts       = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
        var verb     = monitor.DryRun ? "WOULD KILL (DryRun)" : "KILLED";
        var title    = monitor.DryRun ? "Process Memory Alert [DryRun]" : "Process Memory Alert";

        var url = $"https://api.telegram.org/bot{opts.BotToken}/sendMessage";

        // Preferred: MarkdownV2 with every interpolated value escaped.
        var md = new StringBuilder();
        md.AppendLine($"\U0001f6a8 *{EscMd(title)}*");
        md.AppendLine($"\U0001f5a5 Host: `{EscCode(hostName)}`");
        md.AppendLine($"\U0001f4e1 IP:   `{EscCode(hostIp)}`");
        md.AppendLine($"\U0001f550 Time: `{EscCode(ts)}`");
        md.AppendLine($"\U0001f4cb Process: `{EscCode(monitor.ProcessName)}`");
        if (!string.IsNullOrWhiteSpace(monitor.Remark))
            md.AppendLine($"\U0001f4dd Remark: {EscMd(monitor.Remark)}");
        md.AppendLine();
        foreach (var p in killed)
            md.AppendLine($"• PID `{p.Pid}` \\| `{EscCode($"{p.MemoryGb:F2} GB")}` → {EscMd(verb)}");

        // Fallback: no parse_mode at all, so no input can ever make Telegram reject it.
        var plain = new StringBuilder();
        plain.AppendLine($"\U0001f6a8 {title}");
        plain.AppendLine($"\U0001f5a5 Host: {hostName}");
        plain.AppendLine($"\U0001f4e1 IP:   {hostIp}");
        plain.AppendLine($"\U0001f550 Time: {ts}");
        plain.AppendLine($"\U0001f4cb Process: {monitor.ProcessName}");
        if (!string.IsNullOrWhiteSpace(monitor.Remark))
            plain.AppendLine($"\U0001f4dd Remark: {monitor.Remark}");
        plain.AppendLine();
        foreach (var p in killed)
            plain.AppendLine($"• PID {p.Pid} | {p.MemoryGb:F2} GB → {verb}");

        var (ok, status, body) = await PostAsync(url, opts.ChatId, md.ToString().TrimEnd(), "MarkdownV2", logger, ct);

        if (ok)
        {
            logger.LogInformation(
                "Telegram notification sent ({Count} process(es) {Verb})", killed.Count, verb);
            return;
        }

        // 400 means Telegram rejected the formatting. Resend unformatted rather than
        // lose the alert — the kill already happened and must not go unreported.
        if (status == (int)HttpStatusCode.BadRequest)
        {
            logger.LogWarning(
                "Telegram rejected the MarkdownV2 message ({Body}) — retrying as plain text", body);

            var (okPlain, statusPlain, bodyPlain) =
                await PostAsync(url, opts.ChatId, plain.ToString().TrimEnd(), null, logger, ct);

            if (okPlain)
            {
                logger.LogInformation(
                    "Telegram notification sent as plain text ({Count} process(es) {Verb})",
                    killed.Count, verb);
                return;
            }

            logger.LogError("Telegram plain-text fallback also failed {Code}: {Body}", statusPlain, bodyPlain);
            return;
        }

        logger.LogError("Telegram API returned {Code}: {Body}", status, body);
    }

    private static async Task<(bool Ok, int Status, string Body)> PostAsync(
        string url,
        string chatId,
        string text,
        string? parseMode,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            object payload = parseMode is null
                ? new { chat_id = chatId, text }
                : new { chat_id = chatId, text, parse_mode = parseMode };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content, ct);

            if (resp.IsSuccessStatusCode)
                return (true, (int)resp.StatusCode, string.Empty);

            return (false, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram notification");
            return (false, 0, ex.Message);
        }
    }
}
