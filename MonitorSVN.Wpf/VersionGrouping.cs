using System.Collections;
using MonitorSVN.Models;

namespace MonitorSVN;

/// <summary>
/// Ordena a grade agrupando commits da mesma versao (no mesmo dia): primeiro o que esta valendo
/// (Em andamento, Gerado, Erro, Pendente) e abaixo os Ignorados, do mais novo para o mais antigo.
/// Os grupos ficam ordenados pelo commit mais recente.
/// </summary>
public sealed class VersionGrouping : IComparer
{
    private readonly Dictionary<(string Version, DateTime Day), DateTime> _latest = new();

    // grupos expandidos (por versao + dia); o padrao e recolhido
    private readonly HashSet<(string Version, DateTime Day)> _expanded = new();

    public bool IsVisible(LogEntry e) => !e.IsChild || _expanded.Contains((e.Version, e.Timestamp.Date));

    public void Toggle(LogEntry e)
    {
        var key = (e.Version, e.Timestamp.Date);
        if (!_expanded.Remove(key)) _expanded.Add(key);
    }

    public void Rebuild(IEnumerable<LogEntry> entries)
    {
        _latest.Clear();
        var list = entries.ToList();
        foreach (var e in list)
        {
            var key = (e.Version, e.Timestamp.Date);
            _latest[key] = _latest.TryGetValue(key, out var t) && t > e.Timestamp ? t : e.Timestamp;
        }

        foreach (var group in list.GroupBy(e => (e.Version, e.Timestamp.Date)))
        {
            var first = group.OrderBy(e => e, Comparer<LogEntry>.Create(CompareInGroup)).First();
            var count = group.Count() - 1;
            var open = _expanded.Contains(group.Key);
            foreach (var e in group)
            {
                var child = !ReferenceEquals(e, first);
                e.IsChild = child;
                e.HasChildren = !child && count > 0;
                e.ChildCount = child ? 0 : count;
                e.IsExpanded = !child && open;
            }
        }
    }

    public int Compare(object? x, object? y)
    {
        if (x is not LogEntry a || y is not LogEntry b) return 0;

        var la = Latest(a);
        var lb = Latest(b);
        if (la != lb) return lb.CompareTo(la);                                  // grupo mais recente primeiro

        var byVersion = string.CompareOrdinal(a.Version, b.Version);
        if (byVersion != 0) return byVersion;
        if (a.Timestamp.Date != b.Timestamp.Date) return b.Timestamp.CompareTo(a.Timestamp);

        return CompareInGroup(a, b);
    }

    // dentro do grupo: nao ignorados primeiro, depois do mais novo para o mais antigo
    private static int CompareInGroup(LogEntry a, LogEntry b)
    {
        var byRank = Rank(a).CompareTo(Rank(b));
        if (byRank != 0) return byRank;
        var byTime = b.Timestamp.CompareTo(a.Timestamp);
        return byTime != 0 ? byTime : b.RevisionNumber.CompareTo(a.RevisionNumber);
    }

    private static int Rank(LogEntry e) => e.Status == BuildStatus.Ignored ? 1 : 0;

    private DateTime Latest(LogEntry e) =>
        _latest.TryGetValue((e.Version, e.Timestamp.Date), out var t) ? t : e.Timestamp;
}
