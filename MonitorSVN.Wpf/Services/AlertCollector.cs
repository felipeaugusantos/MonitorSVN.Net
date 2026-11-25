using MonitorSVN.Models;

namespace MonitorSVN.Services;

public enum AlertKind { Error, Slow, Success }

public sealed record BuildAlert(AlertKind Kind, LogEntry Entry, string Message);

/// <summary>Decide quais versoes merecem aviso (erro novo, build demorando) e marca cada uma para avisar uma vez so.</summary>
public static class AlertCollector
{
    public const int MinMinutes = 5;
    public const int MaxMinutes = 1440;

    /// <summary>Build em andamento ha mais que isso gera um aviso (padrao; o limite real vem das configuracoes).</summary>
    public static readonly TimeSpan SlowAfter = TimeSpan.FromMinutes(60);

    /// <summary>Le o campo da tela: so aceita inteiro entre 5 minutos e 24 h; senao mantem o valor anterior.</summary>
    public static int ParseMinutes(string? text, int fallback) =>
        int.TryParse(text?.Trim(), out var m) && m is >= MinMinutes and <= MaxMinutes ? m : fallback;

    /// <summary>Erros mais antigos que isso nao avisam (evita uma enxurrada de avisos ao abrir o app depois de um tempo).</summary>
    public static readonly TimeSpan ErrorWindow = TimeSpan.FromHours(6);

    /// <summary>Devolve os avisos novos e marca as linhas (Alerted*), que sao gravadas junto do historico.</summary>
    public static List<BuildAlert> Collect(IEnumerable<LogEntry> entries, DateTime now, TimeSpan? slowAfter = null, bool notifySuccess = false)
    {
        var limit = slowAfter ?? SlowAfter;
        var alerts = new List<BuildAlert>();
        foreach (var e in entries.Where(e => !e.Dismissed).OrderBy(e => e.Timestamp))
        {
            if (e.Status == BuildStatus.Error && !e.AlertedError)
            {
                // sem FinishedAt = erro de antes desta funcao existir: nao avisa
                if (e.FinishedAt is { } done && now - done <= ErrorWindow)
                    alerts.Add(new BuildAlert(AlertKind.Error, e, $"{e.Version} terminou com erro (Rev {e.Revision}, {e.Author})."));
                e.AlertedError = true;
            }
            else if (e.Status == BuildStatus.Generated && !e.AlertedDone)
            {
                // marca sempre, mesmo com o aviso desligado: ligar depois nao dispara avisos de versoes antigas
                if (notifySuccess && e.FinishedAt is { } done && now - done <= ErrorWindow)
                    alerts.Add(new BuildAlert(AlertKind.Success, e, $"{e.Version} foi gerada com sucesso (Rev {e.Revision}, {e.Author})."));
                e.AlertedDone = true;
            }
            else if (e.Status == BuildStatus.InProgress && !e.AlertedSlow && e.StartedAt is { } started && now - started >= limit)
            {
                var minutes = (int)(now - started).TotalMinutes;
                alerts.Add(new BuildAlert(AlertKind.Slow, e,
                    $"{e.Version} está em andamento há {SummaryCalculator.Format(TimeSpan.FromMinutes(minutes))} (Rev {e.Revision})."));
                e.AlertedSlow = true;
            }
        }
        return alerts;
    }

    /// <summary>Texto unico para um balao/banner: ate 3 linhas e "e mais N".</summary>
    public static string Describe(IReadOnlyList<BuildAlert> alerts)
    {
        var lines = alerts.Take(3).Select(a => a.Message).ToList();
        if (alerts.Count > 3) lines.Add($"…e mais {alerts.Count - 3}.");
        return string.Join(Environment.NewLine, lines);
    }

    public static string Title(IReadOnlyList<BuildAlert> alerts)
    {
        var errors = alerts.Count(a => a.Kind == AlertKind.Error);
        var slow = alerts.Count(a => a.Kind == AlertKind.Slow);
        var ok = alerts.Count(a => a.Kind == AlertKind.Success);

        if (errors > 0 && slow > 0 && ok == 0) return $"Monitor SVN: {errors} erro(s) e {slow} build(s) demorando";
        if (errors + slow > 0 && ok > 0)
        {
            var parts = new List<string>();
            if (errors > 0) parts.Add($"{errors} erro(s)");
            if (slow > 0) parts.Add($"{slow} demorando");
            parts.Add($"{ok} gerada(s)");
            return "Monitor SVN: " + string.Join(", ", parts);
        }
        if (errors > 0) return errors == 1 ? "Monitor SVN: versão com erro" : $"Monitor SVN: {errors} versões com erro";
        if (slow > 0) return slow == 1 ? "Monitor SVN: build demorando" : $"Monitor SVN: {slow} builds demorando";
        return ok == 1 ? "Monitor SVN: versão gerada" : $"Monitor SVN: {ok} versões geradas";
    }
}
