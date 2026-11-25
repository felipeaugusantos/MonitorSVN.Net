using System.Text.Json;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class EmailNotifierTests
{
    private static AppSettings Valid() => new()
    {
        SmtpHost = "smtp.exemplo.com", SmtpPort = 587, EmailFrom = "monitor@exemplo.com", EmailTo = "ana@exemplo.com"
    };

    [Fact]
    public void Recipients_accept_semicolon_comma_and_spaces_without_duplicates()
    {
        var list = EmailNotifier.ParseRecipients(" ana@x.com; bia@x.com,\nANA@x.com  cris@x.com ;");
        Assert.Equal(new[] { "ana@x.com", "bia@x.com", "cris@x.com" }, list);
        Assert.Empty(EmailNotifier.ParseRecipients(null));
        Assert.Empty(EmailNotifier.ParseRecipients(" ; "));
    }

    [Theory]
    [InlineData("ana@exemplo.com", true)]
    [InlineData("ana@", false)]
    [InlineData("sem arroba", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("Ana <ana@exemplo.com>", false)]
    public void Address_check(string? text, bool expected) => Assert.Equal(expected, EmailNotifier.IsAddress(text));

    [Fact]
    public void Complete_settings_are_valid() => Assert.Null(EmailNotifier.Validate(Valid()));

    [Fact]
    public void Validate_names_the_first_problem()
    {
        var s = Valid(); s.SmtpHost = " ";
        Assert.Contains("servidor", EmailNotifier.Validate(s));

        s = Valid(); s.SmtpPort = 0;
        Assert.Contains("porta", EmailNotifier.Validate(s));

        s = Valid(); s.SmtpPort = 70000;
        Assert.Contains("porta", EmailNotifier.Validate(s));

        s = Valid(); s.EmailFrom = "nao-e-email";
        Assert.Contains("remetente", EmailNotifier.Validate(s));

        s = Valid(); s.EmailTo = "";
        Assert.Contains("destinatário", EmailNotifier.Validate(s));

        s = Valid(); s.EmailTo = "ana@exemplo.com; lixo";
        Assert.Contains("lixo", EmailNotifier.Validate(s));
    }

    [Fact]
    public async Task Send_refuses_incomplete_settings_before_touching_the_network()
    {
        var s = Valid(); s.EmailTo = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmailNotifier.SendAsync(s, "x", "y"));
    }

    [Fact]
    public void Compose_lists_every_alert_and_reuses_the_balloon_title()
    {
        var now = new DateTime(2026, 11, 24, 15, 0, 0);
        var rows = Enumerable.Range(0, 5).Select(i => new LogEntry
        {
            Revision = "10" + i, Author = "ana", Version = "v" + i, Status = BuildStatus.Error,
            Timestamp = now.AddHours(-2), FinishedAt = now
        }).ToList();
        var alerts = AlertCollector.Collect(rows, now);

        var (subject, body) = EmailNotifier.Compose(alerts, new AppSettings(), now);

        Assert.Equal("Monitor SVN: 5 versões com erro", subject);
        for (var i = 0; i < 5; i++) Assert.Contains("v" + i, body);   // o balao mostra so 3; o e-mail mostra todas
        Assert.DoesNotContain("…e mais", body);
    }

    [Fact]
    public void Smtp_password_is_never_written_in_plain_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msvn-email-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(dir);
            var s = Valid();
            s.EmailEnabled = true;
            s.SmtpPassword = "segredo-super-secreto";
            store.Save(s);

            var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.DoesNotContain("segredo-super-secreto", json);
            Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("SmtpPassword", out _));

            var loaded = store.Load();
            Assert.True(loaded.EmailEnabled);
            Assert.Equal("segredo-super-secreto", loaded.SmtpPassword);
            Assert.Equal("smtp.exemplo.com", loaded.SmtpHost);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Servidor SMTP minimo (sem login/SSL) que guarda a mensagem recebida.</summary>
    private static async Task<string> ReceiveOneMailAsync(System.Net.Sockets.TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        var received = new System.Text.StringBuilder();
        await writer.WriteLineAsync("220 teste");
        var inData = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (inData)
            {
                if (line == ".") { inData = false; await writer.WriteLineAsync("250 ok"); }
                else received.AppendLine(line);
                continue;
            }
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                await writer.WriteLineAsync("250 teste");
            else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase)) { inData = true; await writer.WriteLineAsync("354 go"); }
            else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("221 bye"); break; }
            else { received.AppendLine("CMD " + line); await writer.WriteLineAsync("250 ok"); }
        }
        return received.ToString();
    }

    [Fact]
    public async Task Send_delivers_subject_body_and_every_recipient_over_smtp()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var s = Valid();
            s.SmtpHost = "127.0.0.1";
            s.SmtpPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            s.SmtpSsl = false;
            s.EmailTo = "ana@exemplo.com; bia@exemplo.com";

            var server = ReceiveOneMailAsync(listener);
            await EmailNotifier.SendAsync(s, "Monitor SVN: versão com erro", "379.48.2.30.74.9 terminou com erro.");
            var mail = await server.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("RCPT TO:<ana@exemplo.com>", mail);
            Assert.Contains("RCPT TO:<bia@exemplo.com>", mail);
            Assert.Contains("MAIL FROM:<monitor@exemplo.com>", mail);
            Assert.Contains("379.48.2.30.74.9", mail);
        }
        finally { listener.Stop(); }
    }
}
