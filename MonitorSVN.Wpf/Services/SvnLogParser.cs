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

    /// <summary>
    /// Le a saida de "svn log -v -q --xml" e devolve, para cada commit do dia, as versoes (primeira
    /// pasta abaixo do diretorio monitorado) que ele alterou. Copias (copyfrom) sao ignoradas, como no app Delphi.
    /// </summary>
    public static IReadOnlyList<SvnChange> Parse(string xml, string dir, DateOnly day, IEnumerable<string> ignoredUsers)
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
            if (DateOnly.FromDateTime(timestamp) != day) continue;
            if (ignored.Contains(author)) continue;

            var versions = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var path in entry.Descendants("path"))
            {
                if (path.Attribute("copyfrom-path") != null) continue;

                var text = path.Value;
                var idx = text.IndexOf(dir, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                var segment = text[(idx + dir.Length)..].Split('/')[0];
                if (segment.Length > 0) versions.Add(segment);
            }

            foreach (var v in versions)
                result.Add(new SvnChange(revision, author, timestamp, v));
        }

        return result;
    }
}
