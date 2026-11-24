using System.Net.Http;
using System.Text;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>O que sera (ou foi) alterado em um modelo para incluir uma lib.</summary>
public sealed class TemplatePlan
{
    public required string Template { get; init; }
    public string? Error { get; set; }
    public bool AlreadyExists { get; set; }
    public string? BatPath { get; set; }
    public string? BkpPath { get; set; }
    public string? CmdPath { get; set; }
    public string? NewBat { get; set; }
    public string? NewBkp { get; set; }
    public string? NewCmd { get; set; }
    public string? NewJobConfig { get; set; }
    public string? OldJobConfig { get; set; }
    public bool Ok => Error is null && !AlreadyExists;

    public string Summary
    {
        get
        {
            if (Error is not null) return $"[{Template}] ERRO: {Error}";
            if (AlreadyExists) return $"[{Template}] ja possui a lib; nada a fazer.";
            var lines = new List<string> { $"[{Template}] sera alterado:" };
            if (BatPath is not null) lines.Add($"  - {BatPath} (+ declaracao e bloco de execucao)");
            if (BkpPath is not null) lines.Add($"  - {BkpPath} (+ comando de compilacao)");
            if (CmdPath is not null) lines.Add($"  - {CmdPath} (arquivo novo)");
            if (NewJobConfig is not null) lines.Add($"  - Jenkins: job '{Template}' (+ comando de compilacao)");
            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>Inclui uma lib nova nos modelos: pasta-modelo (.bat, .txt, .cmd) e job-modelo do Jenkins.</summary>
public static class TemplateLibService
{
    private const string BatName = "_FechamentoArquivos.bat";
    private const string BkpName = "_ComandosBKP.txt";

    // Latin1 preserva byte a byte arquivos ANSI/UTF-8 (so o texto inserido, em ASCII, e novo)
    private static readonly Encoding Enc = Encoding.Latin1;

    public static string TemplateFolder(AppSettings settings, string template) =>
        Path.Combine(settings.GenerationRoot, template);

    private static string? FindFile(string folder, string fileName)
    {
        var found = Directory.GetFiles(folder, fileName, SearchOption.AllDirectories);
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>Libs que o modelo ja conhece, lidas do _FechamentoArquivos.bat.</summary>
    public static List<string> ListLibs(AppSettings settings, string template)
    {
        var bat = FindFile(TemplateFolder(settings, template), BatName)
                  ?? throw new FileNotFoundException($"{BatName} nao encontrado (ou duplicado) em {TemplateFolder(settings, template)}");
        return LibTemplateEditor.ListLibsInBat(File.ReadAllText(bat, Enc));
    }

    /// <summary>Calcula tudo em memoria. Nao grava nada.</summary>
    public static async Task<TemplatePlan> PlanAsync(string template, LibSpec lib, AppSettings settings,
        JenkinsClient? jenkins, CancellationToken ct)
    {
        var plan = new TemplatePlan { Template = template };
        try
        {
            var folder = TemplateFolder(settings, template);
            if (!Directory.Exists(folder)) throw new LibEditException($"Pasta-modelo nao encontrada: {folder}");

            var batPath = FindFile(folder, BatName) ?? throw new LibEditException($"{BatName} nao encontrado (ou duplicado) em {folder}");
            var bkpPath = FindFile(folder, BkpName) ?? throw new LibEditException($"{BkpName} nao encontrado (ou duplicado) em {folder}");
            var cmdDir = Path.Combine(folder, "comandosCMD");
            if (!Directory.Exists(cmdDir)) throw new LibEditException($"Pasta comandosCMD nao encontrada em {folder}");

            var bat = File.ReadAllText(batPath, Enc);
            var bkp = File.ReadAllText(bkpPath, Enc);
            var cmdPath = Path.Combine(cmdDir, lib.Name + ".cmd");

            if (LibTemplateEditor.BatHas(bat, lib.Name) || File.Exists(cmdPath))
            {
                plan.AlreadyExists = true;
                return plan;
            }

            // .cmd de referencia: qualquer .cmd de lib que ja tenha o passo de assinatura/zip
            var reference = Directory.GetFiles(cmdDir, "lib*.cmd")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(f => File.ReadAllText(f, Enc).Contains("signtool", StringComparison.OrdinalIgnoreCase))
                ?? throw new LibEditException("Nao achei nenhum lib*.cmd de referencia em comandosCMD.");

            plan.NewBat = LibTemplateEditor.AddToBat(bat, lib.Name);
            plan.NewBkp = LibTemplateEditor.AddToBkp(bkp, lib);
            plan.NewCmd = LibTemplateEditor.CmdFromReference(File.ReadAllText(reference, Enc),
                Path.GetFileNameWithoutExtension(reference), lib.Name);
            plan.BatPath = batPath;
            plan.BkpPath = bkpPath;
            plan.CmdPath = cmdPath;

            if (jenkins is not null)
            {
                var config = await jenkins.GetJobConfigAsync(template, ct);
                plan.OldJobConfig = config;
                plan.NewJobConfig = LibTemplateEditor.AddToJobConfig(config, lib);
            }
        }
        catch (LibEditException ex)
        {
            plan.Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            plan.Error = ex.Message;
        }
        return plan;
    }

    /// <summary>
    /// Grava os planos validos. Guarda copia dos originais, atualiza o Jenkins primeiro (se falhar, nada local mudou)
    /// e so entao os arquivos. Retorna a pasta das copias de seguranca.
    /// </summary>
    public static async Task<string> ApplyAsync(IReadOnlyList<TemplatePlan> plans, string libName,
        JenkinsClient? jenkins, string backupRoot, CancellationToken ct)
    {
        var backup = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + libName);
        Directory.CreateDirectory(backup);

        foreach (var plan in plans.Where(p => p.Ok))
        {
            var dir = Path.Combine(backup, plan.Template);
            Directory.CreateDirectory(dir);
            File.Copy(plan.BatPath!, Path.Combine(dir, BatName));
            File.Copy(plan.BkpPath!, Path.Combine(dir, BkpName));
            if (plan.OldJobConfig is not null)
                File.WriteAllText(Path.Combine(dir, "jenkins-config.xml"), plan.OldJobConfig, new UTF8Encoding(false));
        }

        if (jenkins is not null)
            foreach (var plan in plans.Where(p => p.Ok && p.NewJobConfig is not null))
                await jenkins.UpdateJobConfigAsync(plan.Template, plan.NewJobConfig!, ct);

        foreach (var plan in plans.Where(p => p.Ok))
        {
            File.WriteAllText(plan.BatPath!, plan.NewBat!, Enc);
            File.WriteAllText(plan.BkpPath!, plan.NewBkp!, Enc);
            File.WriteAllText(plan.CmdPath!, plan.NewCmd!, Enc);
            AppLog.Info($"Lib {libName} incluida no modelo {plan.Template} (job do Jenkins: {(plan.NewJobConfig is not null ? "atualizado" : "nao alterado")}).");
        }
        AppLog.Info($"Copia dos originais da inclusao de {libName}: {backup}");
        return backup;
    }
}
