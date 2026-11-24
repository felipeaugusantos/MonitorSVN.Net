using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class AlertCollectorTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 15, 0, 0);

    private static LogEntry E(BuildStatus status, DateTime? started = null, DateTime? finished = null, string version = "v1") =>
        new() { Revision = "10", Author = "ana", Version = version, Status = status, Timestamp = Now.AddHours(-3), StartedAt = started, FinishedAt = finished };

    [Fact]
    public void New_error_alerts_once()
    {
        var e = E(BuildStatus.Error, finished: Now.AddMinutes(-5));

        var first = AlertCollector.Collect(new[] { e }, Now);
        var second = AlertCollector.Collect(new[] { e }, Now);

        var alert = Assert.Single(first);
        Assert.Equal(AlertKind.Error, alert.Kind);
        Assert.Contains("v1", alert.Message);
        Assert.Empty(second);
    }

    [Fact]
    public void Old_errors_are_marked_but_do_not_alert()
    {
        var noEnd = E(BuildStatus.Error);                                              // registro de antes do Termino existir
        var old = E(BuildStatus.Error, finished: Now - AlertCollector.ErrorWindow - TimeSpan.FromMinutes(1));

        Assert.Empty(AlertCollector.Collect(new[] { noEnd, old }, Now));
        Assert.True(noEnd.AlertedError && old.AlertedError);
    }

    [Fact]
    public void Slow_build_alerts_once_after_the_limit()
    {
        var fresh = E(BuildStatus.InProgress, started: Now.AddMinutes(-30), version: "novo");
        var slow = E(BuildStatus.InProgress, started: Now.AddMinutes(-90), version: "lento");

        var alerts = AlertCollector.Collect(new[] { fresh, slow }, Now);

        var a = Assert.Single(alerts);
        Assert.Equal(AlertKind.Slow, a.Kind);
        Assert.Contains("lento", a.Message);
        Assert.Contains("1 h 30 min", a.Message);
        Assert.Empty(AlertCollector.Collect(new[] { fresh, slow }, Now));
    }

    [Fact]
    public void Other_statuses_and_dismissed_rows_never_alert()
    {
        var rows = new[]
        {
            E(BuildStatus.Generated, Now.AddHours(-2), Now.AddHours(-1)), E(BuildStatus.Pending), E(BuildStatus.Ignored),
            E(BuildStatus.InProgress),                                                  // sem Inicio: nao da para medir
            new LogEntry { Status = BuildStatus.Error, FinishedAt = Now, Dismissed = true, Timestamp = Now }
        };
        Assert.Empty(AlertCollector.Collect(rows, Now));
    }

    [Fact]
    public void Title_and_text_summarize_many_alerts()
    {
        var rows = Enumerable.Range(0, 5).Select(i => E(BuildStatus.Error, finished: Now, version: "v" + i)).ToList();
        var alerts = AlertCollector.Collect(rows, Now);

        Assert.Equal("Monitor SVN: 5 versões com erro", AlertCollector.Title(alerts));
        var text = AlertCollector.Describe(alerts);
        Assert.Contains("…e mais 2.", text);
        Assert.Equal(4, text.Split(Environment.NewLine).Length);
        Assert.Equal("Monitor SVN: versão com erro", AlertCollector.Title(alerts.Take(1).ToList()));
    }
}
