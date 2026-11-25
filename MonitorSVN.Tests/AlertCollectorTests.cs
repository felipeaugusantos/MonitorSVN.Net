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

    [Fact]
    public void Slow_limit_can_be_changed()
    {
        var e = E(BuildStatus.InProgress, started: Now.AddMinutes(-20));

        Assert.Empty(AlertCollector.Collect(new[] { e }, Now));                                    // padrao: 60 min
        var alert = Assert.Single(AlertCollector.Collect(new[] { e }, Now, TimeSpan.FromMinutes(15)));
        Assert.Equal(AlertKind.Slow, alert.Kind);
    }

    [Theory]
    [InlineData("30", 60, 30)]
    [InlineData(" 90 ", 60, 90)]
    [InlineData("5", 60, 5)]
    [InlineData("1440", 60, 1440)]
    [InlineData("4", 60, 60)]        // abaixo do minimo
    [InlineData("1441", 45, 45)]     // acima do maximo
    [InlineData("abc", 45, 45)]
    [InlineData("", 45, 45)]
    [InlineData(null, 45, 45)]
    public void Minutes_field_is_validated(string? text, int fallback, int expected) =>
        Assert.Equal(expected, AlertCollector.ParseMinutes(text, fallback));

    [Fact]
    public void Success_alerts_once_only_when_enabled_and_recent()
    {
        var fresh = E(BuildStatus.Generated, Now.AddMinutes(-30), Now.AddMinutes(-5), "ok");

        var alert = Assert.Single(AlertCollector.Collect(new[] { fresh }, Now, notifySuccess: true));
        Assert.Equal(AlertKind.Success, alert.Kind);
        Assert.Contains("ok foi gerada com sucesso", alert.Message);
        Assert.Empty(AlertCollector.Collect(new[] { fresh }, Now, notifySuccess: true));   // so uma vez
    }

    [Fact]
    public void Success_is_marked_even_when_disabled_so_turning_it_on_later_does_not_flood()
    {
        var e = E(BuildStatus.Generated, Now.AddMinutes(-30), Now.AddMinutes(-5));

        Assert.Empty(AlertCollector.Collect(new[] { e }, Now));            // desligado
        Assert.True(e.AlertedDone);
        Assert.Empty(AlertCollector.Collect(new[] { e }, Now, notifySuccess: true));   // ligado depois: nao avisa o passado
    }

    [Fact]
    public void Old_or_unfinished_generated_rows_do_not_alert()
    {
        var noEnd = E(BuildStatus.Generated);
        var old = E(BuildStatus.Generated, finished: Now - AlertCollector.ErrorWindow - TimeSpan.FromMinutes(1));

        Assert.Empty(AlertCollector.Collect(new[] { noEnd, old }, Now, notifySuccess: true));
    }

    [Fact]
    public void Titles_cover_success_alone_and_mixed()
    {
        var rows = new[]
        {
            E(BuildStatus.Generated, finished: Now, version: "a"), E(BuildStatus.Generated, finished: Now, version: "b"),
            E(BuildStatus.Error, finished: Now, version: "c")
        };
        var all = AlertCollector.Collect(rows, Now, notifySuccess: true);

        Assert.Equal("Monitor SVN: 1 erro(s), 2 gerada(s)", AlertCollector.Title(all));
        var good = all.Where(a => a.Kind == AlertKind.Success).ToList();
        Assert.Equal("Monitor SVN: 2 versões geradas", AlertCollector.Title(good));
        Assert.Equal("Monitor SVN: versão gerada", AlertCollector.Title(good.Take(1).ToList()));
    }
}
