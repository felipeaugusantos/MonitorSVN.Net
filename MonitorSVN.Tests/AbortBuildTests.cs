using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

/// <summary>Jenkins de mentira: responde o que a API devolveria e guarda as chamadas feitas.</summary>
internal sealed class FakeJenkins : HttpMessageHandler
{
    public List<string> Calls { get; } = new();
    public string QueueJson { get; set; } = "{\"inQueue\":false}";
    public string BuildsJson { get; set; } = "{\"builds\":[]}";
    public bool JobExists { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        Calls.Add($"{request.Method} {path}{query}");

        HttpResponseMessage Ok(string body = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(body) };

        if (path.EndsWith("crumbIssuer/api/json")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (request.Method == HttpMethod.Get && path.EndsWith("/api/json"))
        {
            if (!JobExists) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (query.Contains("inQueue")) return Task.FromResult(Ok(QueueJson));
            if (query.Contains("builds")) return Task.FromResult(Ok(BuildsJson));
            return Task.FromResult(Ok());
        }
        return Task.FromResult(Ok());
    }
}

public class AbortBuildTests : IDisposable
{
    private const string Version = "379.48.2.30.74.150";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "msvn-abort-" + Guid.NewGuid().ToString("N"));

    public AbortBuildTests() => Directory.CreateDirectory(Path.Combine(_root, Version));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static JenkinsClient Client(FakeJenkins fake) => new("http://jenkins.test/", "", "", fake);

    [Fact]
    public async Task Abort_stops_only_the_builds_that_are_running()
    {
        var fake = new FakeJenkins { BuildsJson = "{\"builds\":[{\"number\":7,\"building\":true},{\"number\":6,\"building\":false}]}" };
        using var jenkins = Client(fake);

        var aborted = await jenkins.AbortBuildAsync(Version, CancellationToken.None);

        Assert.True(aborted);
        Assert.Contains($"POST /job/{Version}/7/stop", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/6/stop"));
    }

    [Fact]
    public async Task Abort_cancels_the_queued_item()
    {
        var fake = new FakeJenkins { QueueJson = "{\"inQueue\":true,\"queueItem\":{\"id\":42}}" };
        using var jenkins = Client(fake);

        Assert.True(await jenkins.AbortBuildAsync(Version, CancellationToken.None));
        Assert.Contains("POST /queue/cancelItem?id=42", fake.Calls);
    }

    [Fact]
    public async Task Abort_with_nothing_running_does_nothing_and_a_missing_job_is_not_an_error()
    {
        var idle = new FakeJenkins();
        using (var jenkins = Client(idle))
            Assert.False(await jenkins.AbortBuildAsync(Version, CancellationToken.None));
        Assert.DoesNotContain(idle.Calls, c => c.StartsWith("POST"));

        using var missing = Client(new FakeJenkins { JobExists = false });
        Assert.False(await missing.AbortBuildAsync(Version, CancellationToken.None));
    }

    private static LogEntry Entry(string rev, BuildStatus status, DateTime? started = null) => new()
    {
        Revision = rev, Author = "ana", Version = Version, Timestamp = DateTime.Now, Status = status, StartedAt = started
    };

    [Fact]
    public async Task A_new_commit_aborts_the_running_build_and_starts_the_new_one()
    {
        var running = Entry("10", BuildStatus.InProgress, DateTime.Now.AddMinutes(-20));
        var fresh = Entry("11", BuildStatus.Pending);
        var fake = new FakeJenkins { BuildsJson = "{\"builds\":[{\"number\":3,\"building\":true}]}" };
        var settings = new AppSettings { GenerationRoot = _root, BuildUrlTemplate = "http://jenkins.test/job/%token%/build?token=%token%" };
        var folder = Path.Combine(_root, Version);
        File.WriteAllText(Path.Combine(folder, MonitorService.ErrorFile), "erro do build abortado");
        var service = new MonitorService(new ObservableCollection<LogEntry> { running, fresh }, () => Client(fake));

        var messages = await service.TriggerPendingAsync(settings, null, CancellationToken.None);

        Assert.Empty(messages);
        // o antigo foi abortado no Jenkins e na grade; o novo disparou
        Assert.Contains($"POST /job/{Version}/3/stop", fake.Calls);
        Assert.Equal(BuildStatus.Ignored, running.Status);
        Assert.Contains("r11", running.ErrorDetail);
        Assert.NotNull(running.FinishedAt);
        Assert.Equal(BuildStatus.InProgress, fresh.Status);
        // ordem: aborta antes de disparar
        var stop = fake.Calls.FindIndex(c => c.Contains("/3/stop"));
        var build = fake.Calls.FindIndex(c => c.Contains("/build?token="));
        Assert.True(stop >= 0 && build > stop, string.Join("\n", fake.Calls));
        // resultado velho do build abortado nao e atribuido ao novo
        Assert.False(File.Exists(Path.Combine(folder, MonitorService.ErrorFile)));
    }

    [Fact]
    public async Task Without_a_running_build_nothing_is_aborted()
    {
        var fresh = Entry("11", BuildStatus.Pending);
        var fake = new FakeJenkins();
        var settings = new AppSettings { GenerationRoot = _root, BuildUrlTemplate = "http://jenkins.test/job/%token%/build?token=%token%" };
        var service = new MonitorService(new ObservableCollection<LogEntry> { fresh }, () => Client(fake));

        await service.TriggerPendingAsync(settings, null, CancellationToken.None);

        Assert.DoesNotContain(fake.Calls, c => c.Contains("/stop") || c.Contains("cancelItem"));
        Assert.Equal(BuildStatus.InProgress, fresh.Status);
    }

    [Fact]
    public async Task If_the_abort_fails_the_old_build_is_kept_and_the_new_commit_stays_pending()
    {
        var running = Entry("10", BuildStatus.InProgress, DateTime.Now.AddMinutes(-20));
        var fresh = Entry("11", BuildStatus.Pending);
        var settings = new AppSettings { GenerationRoot = _root, BuildUrlTemplate = "http://jenkins.test/job/%token%/build?token=%token%" };
        var service = new MonitorService(new ObservableCollection<LogEntry> { running, fresh },
            () => new JenkinsClient("http://jenkins.test/", "", "", new ThrowingOnStop()));

        var messages = await service.TriggerPendingAsync(settings, null, CancellationToken.None);

        Assert.Single(messages);
        Assert.Equal(BuildStatus.InProgress, running.Status);
        Assert.Equal(BuildStatus.Pending, fresh.Status);   // tenta de novo na proxima verificacao
    }

    private sealed class ThrowingOnStop : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/stop")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            if (path.EndsWith("crumbIssuer/api/json")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = request.RequestUri.Query.Contains("builds") ? "{\"builds\":[{\"number\":3,\"building\":true}]}"
                     : request.RequestUri.Query.Contains("inQueue") ? "{\"inQueue\":false}" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
