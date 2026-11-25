using System.Net;
using System.Net.Http;
using System.Text.Json;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class ChatNotifierTests
{
    private const string Hook = "https://chat.googleapis.com/v1/spaces/AAAA/messages?key=k&token=t";

    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status);
        }
    }

    [Theory]
    [InlineData(Hook, true)]
    [InlineData("  " + Hook + " ", true)]
    [InlineData("http://chat.googleapis.com/v1/spaces/A/messages?key=k", false)]   // sem https
    [InlineData("https://outro.exemplo.com/v1/spaces/A/messages", false)]          // outro servidor
    [InlineData("https://chat.googleapis.com.mal.com/x", false)]
    [InlineData("nao e url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_google_chat_https_addresses_are_accepted(string? url, bool expected) =>
        Assert.Equal(expected, ChatNotifier.IsWebhookUrl(url));

    [Fact]
    public void Validate_explains_the_problem()
    {
        Assert.Contains("Informe", ChatNotifier.Validate(new AppSettings { ChatWebhook = " " }));
        Assert.Contains("https://chat.googleapis.com", ChatNotifier.Validate(new AppSettings { ChatWebhook = "http://x" }));
        Assert.Null(ChatNotifier.Validate(new AppSettings { ChatWebhook = Hook }));
    }

    [Fact]
    public async Task Send_posts_the_text_as_json_to_the_webhook()
    {
        var rec = new Recorder();

        await ChatNotifier.SendAsync(new AppSettings { ChatWebhook = Hook }, "olá \"mundo\"\nlinha 2", handler: rec);

        Assert.Equal(HttpMethod.Post, rec.Request!.Method);
        Assert.Equal(Hook, rec.Request.RequestUri!.ToString());
        Assert.Equal("application/json", rec.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("olá \"mundo\"\nlinha 2", JsonDocument.Parse(rec.Body!).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Send_reports_a_refusal_from_google()
    {
        var rec = new Recorder { Status = HttpStatusCode.NotFound };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ChatNotifier.SendAsync(new AppSettings { ChatWebhook = Hook }, "x", handler: rec));

        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task Send_refuses_a_bad_address_without_touching_the_network()
    {
        var rec = new Recorder();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ChatNotifier.SendAsync(new AppSettings { ChatWebhook = "https://outro.exemplo.com/x" }, "x", handler: rec));

        Assert.Null(rec.Request);
    }

    [Fact]
    public void Compose_has_a_bold_title_and_every_alert()
    {
        var now = new DateTime(2026, 11, 25, 15, 0, 0);
        var rows = Enumerable.Range(0, 4).Select(i => new LogEntry
        {
            Revision = "20" + i, Author = "ana", Version = "v" + i, Status = BuildStatus.Error,
            Timestamp = now.AddHours(-2), FinishedAt = now
        }).ToList();

        var text = ChatNotifier.Compose(AlertCollector.Collect(rows, now));

        Assert.StartsWith("*Monitor SVN: 4 versões com erro*", text);
        for (var i = 0; i < 4; i++) Assert.Contains("v" + i, text);
    }

    [Fact]
    public void Webhook_is_never_written_in_plain_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msvn-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(dir);
            store.Save(new AppSettings { ChatEnabled = true, ChatWebhook = Hook });

            var json = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.DoesNotContain("chat.googleapis.com", json);
            Assert.DoesNotContain("token=t", json);

            var loaded = store.Load();
            Assert.True(loaded.ChatEnabled);
            Assert.Equal(Hook, loaded.ChatWebhook);
        }
        finally { Directory.Delete(dir, true); }
    }
}
