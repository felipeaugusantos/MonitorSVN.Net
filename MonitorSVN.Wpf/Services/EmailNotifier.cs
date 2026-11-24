using System.Net;
using System.Net.Mail;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>Aviso por e-mail dos mesmos alertas do balao da bandeja (erro novo, build demorando).</summary>
public static class EmailNotifier
{
    public static List<string> ParseRecipients(string? text) =>
        (text ?? "").Split(new[] { ';', ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsAddress(string? text) =>
        !string.IsNullOrWhiteSpace(text) && MailAddress.TryCreate(text.Trim(), out var a) && a.Address == text.Trim();

    /// <summary>Texto do problema na configuracao, ou nulo se ela esta completa o bastante para enviar.</summary>
    public static string? Validate(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpHost)) return "Informe o servidor SMTP.";
        if (s.SmtpPort is < 1 or > 65535) return "A porta SMTP deve estar entre 1 e 65535.";
        if (!IsAddress(s.EmailFrom)) return "O e-mail do remetente é inválido.";
        var to = ParseRecipients(s.EmailTo);
        if (to.Count == 0) return "Informe pelo menos um destinatário.";
        var bad = to.FirstOrDefault(a => !IsAddress(a));
        return bad is null ? null : $"Destinatário inválido: {bad}";
    }

    /// <summary>Assunto e corpo: todos os avisos (o balao mostra so 3).</summary>
    public static (string Subject, string Body) Compose(IReadOnlyList<BuildAlert> alerts)
    {
        var lines = alerts.Select(a => "• " + a.Message);
        var body = string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine +
                   $"Enviado pelo Monitor SVN em {DateTime.Now:dd/MM/yyyy HH:mm} ({Environment.MachineName}).";
        return (AlertCollector.Title(alerts), body);
    }

    public static async Task SendAsync(AppSettings s, string subject, string body, CancellationToken ct = default)
    {
        if (Validate(s) is { } problem) throw new InvalidOperationException(problem);

        using var message = new MailMessage { From = new MailAddress(s.EmailFrom.Trim()), Subject = subject, Body = body };
        foreach (var to in ParseRecipients(s.EmailTo)) message.To.Add(to);

        using var client = new SmtpClient(s.SmtpHost.Trim(), s.SmtpPort) { EnableSsl = s.SmtpSsl, Timeout = 20000 };
        if (!string.IsNullOrWhiteSpace(s.SmtpUser))
            client.Credentials = new NetworkCredential(s.SmtpUser.Trim(), s.SmtpPassword);
        await client.SendMailAsync(message, ct);
    }
}
