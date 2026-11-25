using System.Net.Http;
using System.Text;
using System.Text.Json;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>Aviso no Google Chat por webhook de entrada de um espaco (mesmos alertas do balao e do e-mail).</summary>
public static class ChatNotifier
{
    public const string WebhookHost = "chat.googleapis.com";

    /// <summary>Aceita so o endereco de webhook do Google Chat (https), para o app nao postar em qualquer lugar.</summary>
    public static bool IsWebhookUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps && u.Host.Equals(WebhookHost, StringComparison.OrdinalIgnoreCase);

    public static string? Validate(AppSettings s) =>
        string.IsNullOrWhiteSpace(s.ChatWebhook) ? "Informe o endereço do webhook do Google Chat."
        : IsWebhookUrl(s.ChatWebhook) ? null
        : $"O endereço deve começar com https://{WebhookHost}/ (copie o webhook do espaço no Chat).";

    /// <summary>Corpo da mensagem: titulo em negrito (*texto* no Chat) e uma linha por aviso.</summary>
    public static string Compose(IReadOnlyList<BuildAlert> alerts)
    {
        var (subject, body) = EmailNotifier.Compose(alerts);
        return "*" + subject + "*" + Environment.NewLine + body;
    }

    public static async Task SendAsync(AppSettings s, string text, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        if (Validate(s) is { } problem) throw new InvalidOperationException(problem);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        var json = JsonSerializer.Serialize(new { text });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(s.ChatWebhook.Trim(), content, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"O Google Chat recusou a mensagem (HTTP {(int)response.StatusCode}). Confira se o webhook ainda existe no espaço.");
    }
}
