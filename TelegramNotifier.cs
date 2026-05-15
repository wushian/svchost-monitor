using System.Text;
using System.Text.Json;

namespace SvchostMonitor;

public static class TelegramNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task SendAsync(
        TelegramOptions opts,
        string processName,
        IReadOnlyList<KilledProcessInfo> killed,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opts.BotToken) || string.IsNullOrWhiteSpace(opts.ChatId))
        {
            logger.LogWarning("Telegram not configured (BotToken/ChatId missing) — skipping notification");
            return;
        }

        var host = Environment.MachineName;
        var ts   = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var sb = new StringBuilder();
        sb.AppendLine($"\U0001f6a8 *Process Memory Alert*");
        sb.AppendLine($"\U0001f5a5 Host: `{host}`");
        sb.AppendLine($"\U0001f550 Time: `{ts}`");
        sb.AppendLine($"\U0001f4cb Process: `{processName}`");
        sb.AppendLine();
        foreach (var p in killed)
            sb.AppendLine($"• PID `{p.Pid}` | `{p.MemoryGb:F2} GB` → KILLED");

        var payload = new
        {
            chat_id    = opts.ChatId,
            text       = sb.ToString().TrimEnd(),
            parse_mode = "Markdown"
        };

        var url = $"https://api.telegram.org/bot{opts.BotToken}/sendMessage";

        try
        {
            var json    = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp    = await _http.PostAsync(url, content, ct);

            if (resp.IsSuccessStatusCode)
                logger.LogInformation("Telegram notification sent ({Count} process(es) killed)", killed.Count);
            else
                logger.LogError("Telegram API returned {Code}: {Body}",
                    (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram notification");
        }
    }
}
