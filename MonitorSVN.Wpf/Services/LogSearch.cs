using System.Text.RegularExpressions;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>
/// Pesquisa do campo "Buscar": revisao, autor ou versao, sem diferenciar maiusculas.
/// Varios termos separados por espaco: todos precisam casar (cada um em qualquer dos tres campos).
/// "r507109" e "Rev: 507109" procuram so na revisao.
/// Exemplo: "renan 150.100" = commits do renan na versao que contem 150.100.
/// </summary>
public sealed class LogSearch
{
    private static readonly Regex RevPrefix = new(@"\brev:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RevToken = new(@"^r(?<n>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly List<(string Text, bool RevisionOnly)> _terms = new();

    public LogSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        var cleaned = RevPrefix.Replace(query, " ");
        foreach (var token in cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var rev = RevToken.Match(token);
            _terms.Add(rev.Success ? (rev.Groups["n"].Value, true) : (token, false));
        }
    }

    /// <summary>false quando o campo esta vazio: nesse caso vale o filtro por data.</summary>
    public bool IsActive => _terms.Count > 0;

    public bool Matches(LogEntry entry) => _terms.All(t => t.RevisionOnly
        ? Has(entry.Revision, t.Text)
        : Has(entry.Revision, t.Text) || Has(entry.Author, t.Text) || Has(entry.Version, t.Text));

    private static bool Has(string? field, string term) =>
        field is not null && field.Contains(term, StringComparison.OrdinalIgnoreCase);
}
