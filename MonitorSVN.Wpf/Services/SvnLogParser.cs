using System.Globalization;
using System.Xml.Linq;

namespace MonitorSVN.Services;

/// <summary>Uma versao tocada por um commit.</summary>
public sealed record SvnChange(string Revision, string Author, DateTime Timestamp, string Version);

public static class SvnLogParser
{
    /// <summary>Garante o formato "/caminho/" e remove a URL base caso o usuario tenha colado o endereco completo.</summary>
    public static string NormalizeDir(string dir, string baseUrl)
    {
        var d = dir.Trim();
        if (!string.IsNullOrEmpty(baseUrl))
            d = d.Replace(baseUrl.Trim(), "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!d.StartsWith('/')) d = "/" + d;
        if (!d.EndsWith('/')) d += "/";
        return d;
    }

    /// <summary>Commits de um unico dia.</summary>
    public static IReadOnlyList<SvnChange> Parse(string xml, string dir, DateOnly day, IEnumerable<string> ignoredUsers) =>
        Parse(xml, dir, day, day, ignoredUsers);

    /// <summary>
    /// Le a saida de "svn log -v -q --xml" e devolve, para cada commit entre <paramref name="from"/> e
    /// <paramref name="to"/> (inclusive, horario local), as versoes (primeira pasta abaixo do diretorio
    /// monitorado) que ele alterou. Copias (copyfrom) sao ignoradas, como no app Delphi, e arquivos soltos
    /// direto no diretorio monitorado nao sao versoes.
    /// </summary>
    public static IReadOnlyList<SvnChange> Parse(string xml, string dir, DateOnly from, DateOnly to,
        IEnumerable<string> ignoredUsers)
    {
        var ignored = new HashSet<string>(ignoredUsers, StringComparer.OrdinalIgnoreCase);
        var result = new List<SvnChange>();
        if (string.IsNullOrWhiteSpace(xml)) return result;

        var doc = XDocument.Parse(xml);
        foreach (var entry in doc.Descendants("logentry"))
        {
            var revision = (string?)entry.Attribute("revision") ?? "";
            var author = (string?)entry.Element("author") ?? "";

            if (!DateTimeOffset.TryParse((string?)entry.Element("date"), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var utc))
                continue;

            var timestamp = utc.LocalDateTime;
            var day = DateOnly.FromDateTime(timestamp);
            if (day < from || day > to) continue;
            if (ignored.Contains(author)) continue;

            var versions = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var path in entry.Descendants("path"))
            {
                if (path.Attribute("copyfrom-path") != null) continue;

                var text = path.Value;
                var idx = text.IndexOf(dir, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                var rest = text[(idx + dir.Length)..];
                var segment = rest.Split('/')[0];
                // arquivo solto no diretorio monitorado (ex.: leiame.txt): nao e uma pasta de versao
                var isLooseFile = !rest.Contains('/') &&
                                  string.Equals((string?)path.Attribute("kind"), "file", StringComparison.OrdinalIgnoreCase);
                if (segment.Length > 0 && !isLooseFile) versions.Add(segment);
            }

            foreach (var v in versions)
                result.Add(new SvnChange(revision, author, timestamp, v));
        }

        return result;
    }
}
