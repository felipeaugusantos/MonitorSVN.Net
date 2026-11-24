using System.Text;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>Gera o CSV do historico (separador ";" e UTF-8 com BOM, que o Excel em português abre direto).</summary>
public static class CsvExporter
{
    public const char Separator = ';';

    private static readonly string[] Header =
    {
        "Revisão", "Autor", "Versão", "Data do commit", "Hora do commit", "Início", "Término",
        "Duração (min)", "Status", "Detalhe do erro", "Provável responsável"
    };

    /// <summary>Linhas dos últimos <paramref name="days"/> dias (pela data do commit), da mais nova para a mais antiga.</summary>
    public static string Build(IEnumerable<LogEntry> entries, DateTime today, int days)
    {
        var from = today.Date.AddDays(-(days - 1));
        var sb = new StringBuilder();
        sb.Append(string.Join(Separator, Header)).Append("\r\n");

        foreach (var e in entries.Where(e => e.Timestamp.Date >= from && e.Timestamp.Date <= today.Date)
                     .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.RevisionNumber))
        {
            var minutes = e.StartedAt is { } s && e.FinishedAt is { } f && f >= s
                ? Math.Round((f - s).TotalMinutes).ToString("0")
                : "";
            var fields = new[]
            {
                e.Revision, e.Author, e.Version, e.Timestamp.ToString("dd/MM/yyyy"), e.Timestamp.ToString("HH:mm"),
                e.StartedAt?.ToString("dd/MM/yyyy HH:mm") ?? "", e.FinishedAt?.ToString("dd/MM/yyyy HH:mm") ?? "",
                minutes, e.StatusText, e.Status == BuildStatus.Error ? e.ErrorDetail ?? "" : "", e.Suspect ?? ""
            };
            sb.Append(string.Join(Separator, fields.Select(Escape))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Aspas quando preciso e prefixo ' em textos que o Excel leria como fórmula (=, +, -, @).</summary>
    public static string Escape(string? value)
    {
        var v = (value ?? "").ReplaceLineEndings(" ");
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return v.Contains(Separator) || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static void Save(string path, string csv) =>
        File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}
