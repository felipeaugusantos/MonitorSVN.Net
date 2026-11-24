using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class SummaryCalculatorTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 15, 0, 0);

    private static LogEntry E(string author, BuildStatus status, int dayOffset = 0, int commitHour = 9,
        int? startMin = null, int? buildMinutes = null)
    {
        var commit = Now.Date.AddDays(dayOffset).AddHours(commitHour);
        var e = new LogEntry { Revision = "1", Author = author, Version = "v", Status = status, Timestamp = commit };
        if (startMin is { } s) e.StartedAt = commit.AddMinutes(s);
        if (buildMinutes is { } b) e.FinishedAt = (e.StartedAt ?? commit).AddMinutes(b);
        return e;
    }

    [Fact]
    public void Counts_by_status_and_period()
    {
        var entries = new[]
        {
            E("a", BuildStatus.Generated), E("a", BuildStatus.Error), E("b", BuildStatus.InProgress),
            E("b", BuildStatus.Pending), E("b", BuildStatus.Ignored),
            E("a", BuildStatus.Generated, dayOffset: -3),
            E("a", BuildStatus.Generated, dayOffset: -40)
        };

        var today = SummaryCalculator.Calculate(entries, Now, 1);
        Assert.Equal(5, today.Commits);
        Assert.Equal((1, 1, 1, 1, 1), (today.Generated, today.Errors, today.InProgress, today.Pending, today.Ignored));

        Assert.Equal(6, SummaryCalculator.Calculate(entries, Now, 7).Commits);
        Assert.Equal(6, SummaryCalculator.Calculate(entries, Now, 30).Commits);
        Assert.Equal(7, SummaryCalculator.Calculate(entries, Now, 90).Commits);
    }

    [Fact]
    public void Error_rate_uses_finished_builds_only()
    {
        var s = SummaryCalculator.Calculate(new[]
        {
            E("a", BuildStatus.Generated), E("a", BuildStatus.Generated), E("a", BuildStatus.Generated),
            E("a", BuildStatus.Error), E("a", BuildStatus.Pending), E("a", BuildStatus.InProgress)
        }, Now, 1);
        Assert.Equal(0.25, s.ErrorRate);
    }

    [Fact]
    public void No_finished_build_means_no_rate_and_no_times()
    {
        var s = SummaryCalculator.Calculate(new[] { E("a", BuildStatus.Pending) }, Now, 1);
        Assert.Null(s.ErrorRate);
        Assert.Null(s.AvgBuild);
        Assert.Null(s.AvgCommitToReady);
        Assert.Equal("—", SummaryCalculator.Format(s.AvgBuild));
    }

    [Fact]
    public void Build_time_averages_only_generated_entries_with_start_and_end()
    {
        var s = SummaryCalculator.Calculate(new[]
        {
            E("a", BuildStatus.Generated, startMin: 10, buildMinutes: 20),
            E("a", BuildStatus.Generated, startMin: 10, buildMinutes: 40),
            E("a", BuildStatus.Generated),                                         // sem Inicio/Termino (registro antigo)
            E("a", BuildStatus.Error, startMin: 10, buildMinutes: 5)               // erro nao entra na media
        }, Now, 1);

        Assert.Equal(TimeSpan.FromMinutes(30), s.AvgBuild);
        Assert.Equal(TimeSpan.FromMinutes(40), s.MaxBuild);
        Assert.Equal(2, s.BuildsMeasured);
        Assert.Equal(TimeSpan.FromMinutes(40), s.AvgCommitToReady);   // (30 + 50) / 2
    }

    [Fact]
    public void Groups_by_day_newest_first_and_by_author_by_commit_count()
    {
        var s = SummaryCalculator.Calculate(new[]
        {
            E("Ana", BuildStatus.Error), E("ana", BuildStatus.Generated), E("Bia", BuildStatus.Generated),
            E("Bia", BuildStatus.Generated, dayOffset: -1)
        }, Now, 7);

        Assert.Equal(new[] { Now.Date, Now.Date.AddDays(-1) }, s.ByDay.Select(d => d.Day));
        Assert.Equal(3, s.ByDay[0].Commits);
        var ana = s.ByAuthor[0];
        Assert.Equal((2, 1), (ana.Commits, ana.Errors));   // "Ana" e "ana" juntos
        Assert.Equal("Bia", s.ByAuthor[1].Author);
    }

    [Theory]
    [InlineData(0, "< 1 min")]
    [InlineData(45, "45 min")]
    [InlineData(65, "1 h 05 min")]
    public void Formats_durations(int minutes, string expected) =>
        Assert.Equal(expected, SummaryCalculator.Format(minutes == 0 ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(minutes)));
}
