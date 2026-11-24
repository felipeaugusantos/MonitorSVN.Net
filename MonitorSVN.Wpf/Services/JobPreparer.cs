using System.Text;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

public sealed record ResolvedRule(string TemplateJob, string FileVersion, string ProductVersion);

/// <summary>Cria, se preciso, o job da versao no Jenkins a partir do modelo e prepara a pasta de geracao.</summary>
public static class JobPreparer
{
    private static readonly string[] TextExtensions = { ".cmd", ".txt", ".bat" };

    public static ResolvedRule Resolve(string version, IEnumerable<VersionRule> rules)
    {
        var rule = rules.FirstOrDefault(r => r.Pattern.Length == 0 ||
                       version.Contains(r.Pattern, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("Nenhuma regra de versao configurada.");

        var product = !string.IsNullOrEmpty(rule.ProductVersion)
            ? rule.ProductVersion
            : (version.Length > 7 ? version[7..] : version);

        return new ResolvedRule(rule.TemplateJob, rule.FileVersion, product);
    }

    /// <summary>Retorna true quando o job ja existe ou foi criado com sucesso.</summary>
    public static async Task EnsureAsync(string version, AppSettings settings, JenkinsClient jenkins, CancellationToken ct)
    {
        if (await jenkins.JobExistsAsync(version, ct)) return;

        var rule = Resolve(version, settings.Rules);

        var config = await jenkins.GetJobConfigAsync(rule.TemplateJob, ct);
        config = config.Replace("releaseversion", version, StringComparison.OrdinalIgnoreCase);
        await jenkins.CreateJobAsync(version, config, ct);

        var source = Path.Combine(settings.GenerationRoot, rule.TemplateJob);
        var target = Path.Combine(settings.GenerationRoot, version);
        var replacements = new (string From, string To)[]
        {
            ("releaseversion", version),
            ("fileversion", rule.FileVersion),
            ("productversion", rule.ProductVersion)
        };
        await Task.Run(() => CopyDirectory(source, target, replacements), ct);
    }

    /// <summary>Copia a pasta-modelo trocando os marcadores apenas nos arquivos .cmd/.txt/.bat.</summary>
    public static void CopyDirectory(string source, string target, (string From, string To)[] replacements)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Pasta-modelo nao encontrada: {source}");

        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            var ext = Path.GetExtension(file);

            if (TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                // Latin1 preserva byte a byte arquivos ANSI/UTF-8 (so os marcadores, em ASCII, sao trocados)
                var text = File.ReadAllText(file, Encoding.Latin1);
                foreach (var (from, to) in replacements)
                    if (from.Length > 0 && to.Length > 0)
                        text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(destination, text, Encoding.Latin1);
            }
            else
            {
                File.Copy(file, destination, overwrite: true);
            }
        }

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), replacements);
    }
}
