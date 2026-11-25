using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>
/// Encadeamento automatico: versao gerada -> fechamento (automatico) -> copia (so apos confirmacao do operador,
/// porque a copia remove os ZIPs da origem).
/// </summary>
public static class AutoChain
{
    /// <summary>So versoes que terminaram agora: ligar o recurso nao fecha o historico antigo.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(30);

    /// <summary>Linhas Gerado que ainda nao tiveram o fechamento automatico tentado (uma tentativa por linha).</summary>
    public static List<LogEntry> SelectForClosing(IEnumerable<LogEntry> entries, DateTime now, TimeSpan? window = null)
    {
        var limit = now - (window ?? FreshWindow);
        return entries
            .Where(e => e.Status == BuildStatus.Generated && !e.AutoClosed && !e.Dismissed
                        && e.FinishedAt is { } finished && finished >= limit)
            .OrderBy(e => e.FinishedAt)
            .ToList();
    }
}
