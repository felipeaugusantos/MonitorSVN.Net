using MonitorSVN.Models;

namespace MonitorSVN.Services;

public sealed record DayStat(DateTime Day, int Commits, int Generated, int Errors, TimeSpan? AvgBuild)
{
    public string DayText => Day.ToString("dd/MM/yyyy (ddd)");
    public string AvgText => SummaryCalculator.Format(AvgBuild);
}

public sealed record AuthorStat(string Author, int Commits, int Errors);

public sealed class Summary
{
    public int Days { get; init; }
    public DateTime From { get; init; }
    public int Commits { get; init; }
    public int Generated { get; init; }
    public int Errors { get; init; }
    public int InProgress { get; init; }
    public int Pending { get; init; }
    public int Ignored { get; init; }

    /// <summary>Erros / (Gerados + Erros). Nulo quando nenhuma geracao terminou no periodo.</summary>
    public double? ErrorRate { get; init; }

    /// <summary>Tempo entre disparar o build e a geracao terminar (so versoes Geradas, com Inicio e Termino gravados).</summary>
    public TimeSpan? AvgBuild { get; init; }
    public TimeSpan? MaxBuild { get; init; }
    public int BuildsMeasured { get; init; }

    /// <summary>Tempo entre o commit e a versao ficar pronta (so versoes Geradas com Termino gravado).</summary>
    public TimeSpan? AvgCommitToReady { get; init; }

    public IReadOnlyList<DayStat> ByDay { get; init; } = Array.Empty<DayStat>();
    public IReadOnlyList<AuthorStat> ByAuthor { get; init; } = Array.Empty<AuthorStat>();
}

public static class SummaryCalculator
{
    /// <summary>Resumo dos ultimos <paramref name="days"/> dias (1 = so hoje), contando pela data do commit.</summary>
    public static Summary Calculate(IEnumerable<LogEntry> entries, DateTime today, int days)
    {
        var from = today.Date.AddDays(-(days - 1));
        var list = entries.Where(e => e.Timestamp.Date >= from && e.Timestamp.Date <= today.Date).ToList();

        var generated = list.Where(e => e.Status == BuildStatus.Generated).ToList();
        var errors = list.Count(e => e.Status == BuildStatus.Error);

        return new Summary
        {
            Days = days,
            From = from,
            Commits = list.Count,
            Generated = generated.Count,
            Errors = errors,
            InProgress = list.Count(e => e.Status == BuildStatus.InProgress),
            Pending = list.Count(e => e.Status == BuildStatus.Pending),
            Ignored = list.Count(e => e.Status == BuildStatus.Ignored),
            ErrorRate = generated.Count + errors == 0 ? null : (double)errors / (generated.Count + errors),
            AvgBuild = Average(BuildTimes(generated)),
            MaxBuild = BuildTimes(generated) is { Count: > 0 } t ? t.Max() : null,
            BuildsMeasured = BuildTimes(generated).Count,
            AvgCommitToReady = Average(generated
                .Where(e => e.FinishedAt is { } f && f >= e.Timestamp)
                .Select(e => e.FinishedAt!.Value - e.Timestamp).ToList()),
            ByDay = list.GroupBy(e => e.Timestamp.Date).OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    var gen = g.Where(e => e.Status == BuildStatus.Generated).ToList();
                    return new DayStat(g.Key, g.Count(), gen.Count, g.Count(e => e.Status == BuildStatus.Error),
                        Average(BuildTimes(gen)));
                }).ToList(),
            ByAuthor = list.GroupBy(e => e.Author, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AuthorStat(g.Key, g.Count(), g.Count(e => e.Status == BuildStatus.Error)))
                .OrderByDescending(a => a.Commits).ThenBy(a => a.Author, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static List<TimeSpan> BuildTimes(IEnumerable<LogEntry> generated) =>
        generated.Where(e => e.StartedAt is { } s && e.FinishedAt is { } f && f >= s)
            .Select(e => e.FinishedAt!.Value - e.StartedAt!.Value).ToList();

    private static TimeSpan? Average(List<TimeSpan> times) =>
        times.Count == 0 ? null : TimeSpan.FromTicks((long)times.Average(t => t.Ticks));

    public static string Format(TimeSpan? t)
    {
        if (t is not { } v) return "—";
        if (v.TotalMinutes < 1) return "< 1 min";
        return v.TotalHours < 1 ? $"{(int)v.TotalMinutes} min" : $"{(int)v.TotalHours} h {v.Minutes:00} min";
    }
}
