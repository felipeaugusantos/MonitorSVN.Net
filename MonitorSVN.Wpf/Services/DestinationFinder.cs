namespace MonitorSVN.Services;

/// <summary>
/// Procura, abaixo das raizes de destino, as pastas com o nome da versao. Diferente do VarreduraSistema, tem limite
/// de profundidade, para de descer ao achar a pasta e pode ser cancelada (a busca em rede pode demorar).
/// </summary>
public static class DestinationFinder
{
    public const int DefaultMaxDepth = 6;

    public static List<string> Find(IEnumerable<string> roots, IReadOnlyCollection<string> names,
        IProgress<string>? progress, CancellationToken ct, int maxDepth = DefaultMaxDepth)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (var root in roots)
        {
            if (Directory.Exists(root)) Walk(root, 0);
        }
        return found;

        void Walk(string folder, int depth)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(folder);
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(folder).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Busca de destino: erro ao acessar {folder}: {ex.Message}");
                return;
            }

            foreach (var child in children)
            {
                if (wanted.Contains(Path.GetFileName(child))) { found.Add(child); continue; }
                if (depth + 1 < maxDepth) Walk(child, depth + 1);
            }
        }
    }
}
