using System.Text;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

public sealed record ResolvedRule(string TemplateJob, string FileVersion, string ProductVersion);

/// <summary>Cria, se preciso, o job da versao no Jenkins a partir do modelo e prepara a pasta de geracao.</summary>
public static class JobPreparer
{
    private static readonly string[] TextExtensions = { ".cmd", ".txt", ".bat" };

    /// <summary>Gravado na pasta de geracao durante a copia; se sobrar (copia interrompida), a pasta e copiada de novo.</summary>
    public const string IncompleteMarker = ".monitorsvn-copiando";

    public static ResolvedRule Resolve(string version, IEnumerable<VersionRule> rules)
    {
        var rule = rules.FirstOrDefault(r => r.Pattern.Length == 0 || ContainsWhole(version, r.Pattern))
                   ?? throw new InvalidOperationException("Nenhuma regra de versao configurada.");

        var product = !string.IsNullOrEmpty(rule.ProductVersion)
            ? rule.ProductVersion
            : (version.Length > 7 ? version[7..] : version);

        return new ResolvedRule(rule.TemplateJob, rule.FileVersion, product);
    }

    /// <summary>
    /// true se <paramref name="pattern"/> aparece em <paramref name="version"/> sem estar colado a outros digitos
    /// (ex.: "379.48.2.30.74.150" casa com "379.48.2.30.74.150" e "379.48.2.30.74.150.3", mas nao com "...74.1500").
    /// </summary>
    public static bool ContainsWhole(string version, string pattern)
    {
        var start = 0;
        while ((start = version.IndexOf(pattern, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = start + pattern.Length;
            var digitBefore = start > 0 && char.IsDigit(version[start - 1]) && char.IsDigit(pattern[0]);
            var digitAfter = end < version.Length && char.IsDigit(version[end]) && char.IsDigit(pattern[^1]);
            if (!digitBefore && !digitAfter) return true;
            start++;
        }
        return false;
    }

    /// <summary>A pasta de geracao existe e a copia dela terminou.</summary>
    public static bool FolderReady(string target) =>
        Directory.Exists(target) && !File.Exists(Path.Combine(target, IncompleteMarker));

    /// <summary>
    /// Garante a pasta de geracao e o job da versao. A pasta e copiada ANTES de criar o job: se a copia falhar,
    /// o job nao existe e tudo e tentado de novo na proxima verificacao. Uma copia interrompida (marcador
    /// presente) tambem e refeita, mesmo com o job ja criado.
    /// </summary>
    public static async Task EnsureAsync(string version, AppSettings settings, JenkinsClient jenkins, CancellationToken ct)
    {
        var target = Path.Combine(settings.GenerationRoot, version);
        var jobExists = await jenkins.JobExistsAsync(version, ct);
        if (jobExists && FolderReady(target)) return;

        var rule = Resolve(version, settings.Rules);

        var source = Path.Combine(settings.GenerationRoot, rule.TemplateJob);
        var replacements = new (string From, string To)[]
        {
            ("releaseversion", version),
            ("fileversion", rule.FileVersion),
            ("productversion", rule.ProductVersion)
        };
        if (!jobExists || !FolderReady(target))
        {
            await Task.Run(() => CopyTemplateFolder(source, target, replacements), ct);
            AppLog.Info($"Pasta de geracao preparada: {target}");
        }

        if (!jobExists)
        {
            var config = await jenkins.GetJobConfigAsync(rule.TemplateJob, ct);
            config = config.Replace("releaseversion", version, StringComparison.OrdinalIgnoreCase);
            await jenkins.CreateJobAsync(version, config, ct);
            AppLog.Info($"Job criado no Jenkins: {version} (modelo {rule.TemplateJob}).");
        }
    }

    /// <summary>Copia a pasta-modelo marcando a pasta como incompleta ate o fim da copia.</summary>
    public static void CopyTemplateFolder(string source, string target, (string From, string To)[] replacements)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Pasta-modelo nao encontrada: {source}");

        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, IncompleteMarker);
        File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        CopyDirectory(source, target, replacements);
        File.Delete(marker);
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
