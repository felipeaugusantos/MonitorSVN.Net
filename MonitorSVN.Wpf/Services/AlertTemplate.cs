using System.Text.RegularExpressions;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>
/// Modelo de texto de UMA linha de aviso (uma por versao). Campos entre chaves, ex.: {versao}. Um trecho entre
/// colchetes, ex.: [ Responsável: {responsavel}], so aparece se todos os campos dele tiverem valor.
/// </summary>
public static class AlertTemplate
{
    public const string DefaultLine = "{versao} {situacao} (Rev {revisao}, {autor}).[ Provável responsável: {responsavel}.]";

    public static readonly string[] Fields =
        { "versao", "revisao", "autor", "tipo", "situacao", "responsavel", "tempo", "hora", "maquina", "link" };

    private static readonly Regex Field = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex Optional = new(@"\[([^\[\]]*)\]", RegexOptions.Compiled);

    /// <summary>Texto do modelo: o configurado ou, se vazio, o padrao.</summary>
    public static string Or(string? template) => string.IsNullOrWhiteSpace(template) ? DefaultLine : template;

    /// <summary>Primeiro campo que nao existe (erro de digitacao), ou nulo.</summary>
    public static string? UnknownField(string? template) =>
        Field.Matches(template ?? "").Select(m => m.Groups[1].Value)
            .FirstOrDefault(f => !Fields.Contains(f, StringComparer.OrdinalIgnoreCase));

    public static Dictionary<string, string> Values(BuildAlert alert, AppSettings settings, DateTime now)
    {
        var e = alert.Entry;
        var isError = alert.Kind == AlertKind.Error;
        var isSlow = alert.Kind == AlertKind.Slow;
        var elapsed = !isSlow
            ? (e.StartedAt is { } s && e.FinishedAt is { } f && f >= s ? f - s : (TimeSpan?)null)
            : (e.StartedAt is { } st ? now - st : (TimeSpan?)null);
        var tempo = elapsed is { } t ? SummaryCalculator.Format(TimeSpan.FromMinutes((int)t.TotalMinutes)) : "";

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["versao"] = e.Version,
            ["revisao"] = e.Revision,
            ["autor"] = e.Author,
            ["tipo"] = isError ? "erro" : isSlow ? "demorando" : "gerada",
            ["situacao"] = isError ? "terminou com erro"
                : isSlow ? "está em andamento" + (tempo.Length > 0 ? " há " + tempo : "")
                : "foi gerada com sucesso",
            ["responsavel"] = isError ? (e.Suspect ?? "") : "",
            ["tempo"] = tempo,
            ["hora"] = (isSlow ? now : e.FinishedAt ?? now).ToString("HH:mm"),
            ["maquina"] = Environment.MachineName,
            ["link"] = JenkinsClient.JobUrl(settings.JenkinsUrl, e.Version)?.ToString() ?? ""
        };
    }

    public static string Render(string? template, IReadOnlyDictionary<string, string> values)
    {
        string Value(string name) => values.TryGetValue(name, out var v) ? v : "{" + name + "}";

        // trechos opcionais: somem inteiros se algum campo deles estiver vazio
        var text = Optional.Replace(Or(template), m =>
        {
            var inner = m.Groups[1].Value;
            var empty = Field.Matches(inner).Any(f => values.TryGetValue(f.Groups[1].Value, out var v) && string.IsNullOrWhiteSpace(v));
            return empty ? "" : inner;
        });
        return Field.Replace(text, m => Value(m.Groups[1].Value)).Trim();
    }

    /// <summary>Linha de cada aviso pelo modelo + rodape; o titulo com o total continua automatico.</summary>
    public static string Line(string? template, BuildAlert alert, AppSettings settings, DateTime now) =>
        Render(template, Values(alert, settings, now));

    /// <summary>Exemplo para a janela de configuracao (valores de mentira).</summary>
    public static string Preview(string? template, AppSettings settings)
    {
        var entry = new LogEntry
        {
            Version = "379.48.2.30.74.9", Revision = "507447", Author = "felipe.santos", Status = BuildStatus.Error,
            Suspect = "danilo (Rev 507440, uFrmPrincipal.pas)",
            StartedAt = DateTime.Now.AddMinutes(-23), FinishedAt = DateTime.Now
        };
        return Line(template, new BuildAlert(AlertKind.Error, entry, ""), settings, DateTime.Now);
    }
}
