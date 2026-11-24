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

    /// <summary>Remocao: o plano apaga o .cmd da lib (CmdPath) em vez de criar.</summary>
    public bool IsRemoval { get; set; }
    public bool DeleteCmd { get; set; }

    /// <summary>Remocao: a lib nao esta em nenhum dos lugares.</summary>
    public bool NotFound { get; set; }

    /// <summary>Remocao: o que sera tirado, em texto, para o resumo.</summary>
    public List<string> Actions { get; } = new();
    public string? BatPath { get; set; }
    public string? BkpPath { get; set; }
    public string? CmdPath { get; set; }
    public string? NewBat { get; set; }
    public string? NewBkp { get; set; }
    public string? NewCmd { get; set; }
    public string? NewJobConfig { get; set; }
    public string? OldJobConfig { get; set; }
    public bool Ok => Error is null && !AlreadyExists && !NotFound;

    public string Summary
    {
        get
        {
            if (Error is not null) return $"[{Template}] ERRO: {Error}";
            if (AlreadyExists) return $"[{Template}] ja possui a lib; nada a fazer.";
            if (NotFound) return $"[{Template}] nao possui a lib; nada a remover.";
            if (IsRemoval) return $"[{Template}] sera alterado:" + string.Concat(Actions.Select(a => Environment.NewLine + "  - " + a));
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

    /// <summary>
    /// Versoes do dia que ja tem pasta de geracao e job proprios: candidatas a receber a lib. Fica de fora (em Skipped,
    /// com o motivo) a versao com build em andamento. Versoes ainda sem pasta nascem do modelo, que ja tera a lib.
    /// </summary>
    public static (List<string> Versions, List<string> Skipped) DayVersions(
        IEnumerable<LogEntry> entries, AppSettings settings, DateTime today)
    {
        var versions = new List<string>();
        var skipped = new List<string>();
        var groups = entries.Where(e => !e.Dismissed && e.Timestamp.Date == today.Date)
            .GroupBy(e => e.Version).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            if (!JobPreparer.FolderReady(Path.Combine(settings.GenerationRoot, g.Key))) continue;
            if (g.Any(e => e.Status == BuildStatus.InProgress))
                skipped.Add($"[{g.Key}] ignorada: build em andamento. Inclua a lib depois que terminar.");
            else
                versions.Add(g.Key);
        }
        return (versions, skipped);
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
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            plan.Error = "Jenkins: tempo esgotado aguardando resposta.";
        }
        return plan;
    }

    /// <summary>Calcula, sem gravar, o que sai do modelo ao remover a lib: .bat, .txt, .cmd e o job do Jenkins.</summary>
    public static async Task<TemplatePlan> PlanRemoveAsync(string template, string name, AppSettings settings,
        JenkinsClient? jenkins, CancellationToken ct)
    {
        var plan = new TemplatePlan { Template = template, IsRemoval = true };
        try
        {
            LibTemplateEditor.ValidateName(name);
            var folder = TemplateFolder(settings, template);
            if (!Directory.Exists(folder)) throw new LibEditException($"Pasta-modelo nao encontrada: {folder}");

            var batPath = FindFile(folder, BatName) ?? throw new LibEditException($"{BatName} nao encontrado (ou duplicado) em {folder}");
            var bkpPath = FindFile(folder, BkpName) ?? throw new LibEditException($"{BkpName} nao encontrado (ou duplicado) em {folder}");
            var cmdPath = Path.Combine(folder, "comandosCMD", name + ".cmd");

            var bat = File.ReadAllText(batPath, Enc);
            var bkp = File.ReadAllText(bkpPath, Enc);
            var inBat = LibTemplateEditor.BatHas(bat, name);
            var inBkp = LibTemplateEditor.BkpHas(bkp, name);
            var hasCmd = File.Exists(cmdPath);

            string? config = null;
            var inJob = false;
            if (jenkins is not null)
            {
                config = await jenkins.GetJobConfigAsync(template, ct);
                inJob = LibTemplateEditor.JobConfigHas(config, name);
            }

            if (!inBat && !inBkp && !hasCmd && !inJob)
            {
                plan.NotFound = true;
                return plan;
            }

            plan.BatPath = batPath;
            plan.BkpPath = bkpPath;
            plan.CmdPath = cmdPath;
            if (inBat)
            {
                plan.NewBat = LibTemplateEditor.RemoveFromBat(bat, name);
                plan.Actions.Add($"{batPath} (- declaracao e bloco de execucao)");
            }
            if (inBkp)
            {
                plan.NewBkp = LibTemplateEditor.RemoveFromBkp(bkp, name);
                plan.Actions.Add($"{bkpPath} (- comando de compilacao)");
            }
            if (hasCmd)
            {
                plan.DeleteCmd = true;
                plan.Actions.Add($"{cmdPath} (arquivo removido; copia nos backups)");
            }
            if (config is not null)
            {
                plan.OldJobConfig = config;
                if (inJob)
                {
                    plan.NewJobConfig = LibTemplateEditor.RemoveFromJobConfig(config, name);
                    plan.Actions.Add($"Jenkins: job '{template}' (- comando de compilacao)");
                }
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
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            plan.Error = "Jenkins: tempo esgotado aguardando resposta.";
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
        var backup = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + (plans.Any(p => p.IsRemoval) ? "-remover-" : "-") + libName);
        Directory.CreateDirectory(backup);

        foreach (var plan in plans.Where(p => p.Ok))
        {
            var dir = Path.Combine(backup, plan.Template);
            Directory.CreateDirectory(dir);
            File.Copy(plan.BatPath!, Path.Combine(dir, BatName));
            File.Copy(plan.BkpPath!, Path.Combine(dir, BkpName));
            if (plan.DeleteCmd && File.Exists(plan.CmdPath!))
                File.Copy(plan.CmdPath!, Path.Combine(dir, Path.GetFileName(plan.CmdPath!)));
            if (plan.OldJobConfig is not null)
                File.WriteAllText(Path.Combine(dir, "jenkins-config.xml"), plan.OldJobConfig, new UTF8Encoding(false));
        }

        if (jenkins is not null)
            await UpdateJobsOrRollBackAsync(plans, jenkins, backup, ct);

        foreach (var plan in plans.Where(p => p.Ok))
        {
            if (plan.NewBat is not null) File.WriteAllText(plan.BatPath!, plan.NewBat, Enc);
            if (plan.NewBkp is not null) File.WriteAllText(plan.BkpPath!, plan.NewBkp, Enc);
            if (plan.NewCmd is not null) File.WriteAllText(plan.CmdPath!, plan.NewCmd, Enc);
            if (plan.DeleteCmd) File.Delete(plan.CmdPath!);
            AppLog.Info($"Lib {libName} {(plan.IsRemoval ? "removida" : "incluida")} no modelo {plan.Template} (job do Jenkins: {(plan.NewJobConfig is not null ? "atualizado" : "nao alterado")}).");
        }
        AppLog.Info($"Copia dos originais da alteracao de {libName}: {backup}");
        return backup;
    }

    /// <summary>
    /// Atualiza os jobs-modelo um a um. Se um falhar, volta os ja atualizados ao config original: sem isso,
    /// o job ficaria com a lib e a pasta-modelo sem, e a proxima tentativa recusaria o modelo ("ja existe").
    /// </summary>
    private static async Task UpdateJobsOrRollBackAsync(IReadOnlyList<TemplatePlan> plans, JenkinsClient jenkins,
        string backup, CancellationToken ct)
    {
        var updated = new List<TemplatePlan>();
        foreach (var plan in plans.Where(p => p.Ok && p.NewJobConfig is not null))
        {
            try
            {
                await jenkins.UpdateJobConfigAsync(plan.Template, plan.NewJobConfig!, ct);
                updated.Add(plan);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                var notRestored = new List<string>();
                foreach (var done in updated)
                {
                    try
                    {
                        // CancellationToken.None: a reversao precisa terminar mesmo que a operacao tenha sido cancelada
                        await jenkins.UpdateJobConfigAsync(done.Template, done.OldJobConfig!, CancellationToken.None);
                        AppLog.Info($"Job '{done.Template}' restaurado apos falha em '{plan.Template}'.");
                    }
                    catch (Exception rollbackEx) when (rollbackEx is HttpRequestException or TaskCanceledException)
                    {
                        AppLog.Error($"Nao foi possivel restaurar o job '{done.Template}'.", rollbackEx);
                        notRestored.Add(done.Template);
                    }
                }

                var reason = ex is TaskCanceledException ? "tempo esgotado aguardando resposta" : ex.Message;
                var message = $"Falha ao atualizar o job '{plan.Template}' no Jenkins: {reason}. Nenhum arquivo local foi alterado.";
                if (updated.Count > 0)
                    message += notRestored.Count == 0
                        ? " Os jobs já atualizados foram restaurados."
                        : $" ATENÇÃO: não foi possível restaurar o(s) job(s) {string.Join(", ", notRestored)}; " +
                          $"o config.xml original está em {backup}.";
                AppLog.Warn(message, ex);
                throw new LibEditException(message);
            }
        }
    }
}
