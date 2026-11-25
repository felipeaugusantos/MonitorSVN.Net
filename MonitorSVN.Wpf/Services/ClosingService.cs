namespace MonitorSVN.Services;

public enum ClosingState { Queued, Running, Done, Error, Cancelled }

public sealed class ClosingJob
{
    public required string Key { get; init; }
    public required string ProjectPath { get; init; }
    public required string Title { get; init; }
    public required string BatPath { get; init; }
    public ClosingState State { get; set; } = ClosingState.Queued;
    public int? Percent { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Message { get; set; } = "";
    public string? LogPath { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
    internal ProjectLock? Lock { get; set; }

    public string Name => Path.GetFileName(ProjectPath.TrimEnd('\\', '/'));
}

/// <summary>
/// Fila de fechamentos de versao (Fechamento Local / Cloud). Porta do VarreduraSistema: valida a pasta, trava o
/// projeto para os outros operadores, roda o _FechamentoArquivos.bat (ate N por vez) e so considera concluido
/// quando nao sobra nenhum .exe/.dll/.bpl na raiz (o codigo de saida do BAT nao e confiavel).
/// Sem dependencia de tela: quem chama consulta <see cref="ProgressText"/> periodicamente.
/// </summary>
public sealed class ClosingService
{
    public const string BatFileName = "_FechamentoArquivos.bat";
    public const string CommandsFolder = "comandosCMD";
    public const int DefaultMaxConcurrent = 2;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, ClosingJob> _jobs = new();
    private readonly SemaphoreSlim _slots;
    private readonly string _logFolder;
    private readonly TimeSpan _timeout;

    /// <summary>Disparado (em thread de fundo) quando um fechamento termina, com qualquer resultado.</summary>
    public event Action<ClosingJob>? Finished;

    public ClosingService(string logFolder, int maxConcurrent = DefaultMaxConcurrent, TimeSpan? timeout = null)
    {
        _logFolder = logFolder;
        _slots = new SemaphoreSlim(maxConcurrent);
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Projetos com _CLOUD no nome da pasta sao Cloud; os demais, Local.</summary>
    public static bool IsCloud(string folderName) => folderName.EndsWith("_CLOUD", StringComparison.OrdinalIgnoreCase);

    public static string TitleFor(bool cloud) => cloud ? "Fechamento Cloud" : "Fechamento Local";

    /// <returns>O motivo de nao poder fechar, ou null se a pasta esta apta (nao considera a fila).</returns>
    public static string? BlockReason(string projectPath, bool expectCloud)
    {
        var name = Path.GetFileName(projectPath.TrimEnd('\\', '/'));
        if (!Directory.Exists(projectPath)) return $"pasta nao encontrada: {projectPath}";
        if (IsCloud(name) != expectCloud)
            return expectCloud ? "projeto local nao pode usar fechamento cloud" : "projeto CLOUD nao pode usar fechamento local";

        var zipErrors = ZipNameValidator.Validate(projectPath);
        if (zipErrors.Count > 0)
            return $"{zipErrors.Count} ZIP(s) com nome incorreto: " + string.Join("; ", zipErrors.Take(3));

        var commands = Path.Combine(projectPath, CommandsFolder);
        if (!Directory.Exists(commands)) return $"pasta {CommandsFolder} nao encontrada: {commands}";
        var bat = Path.Combine(commands, BatFileName);
        if (!File.Exists(bat)) return $"BAT nao encontrado: {bat}";
        return null;
    }

    /// <summary>Arquivos que o BAT ainda nao compactou (vazio = fechamento completo).</summary>
    public static List<string> PendingFiles(string projectPath) =>
        Directory.EnumerateFiles(projectPath, "*", SearchOption.TopDirectoryOnly)
            .Where(p => ClosingRunner.PendingExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Select(p => Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string KeyOf(string projectPath) => Path.GetFullPath(projectPath).TrimEnd('\\', '/').ToLowerInvariant();

    public bool IsActive(string projectPath)
    {
        lock (_gate) return _jobs.TryGetValue(KeyOf(projectPath), out var job) && IsActiveState(job.State);
    }

    private static bool IsActiveState(ClosingState s) => s is ClosingState.Queued or ClosingState.Running;

    public int ActiveCount
    {
        get { lock (_gate) return _jobs.Values.Count(j => IsActiveState(j.State)); }
    }

    /// <summary>Cancela todos (ao sair do app: o BAT nao pode ficar rodando sem dono nem trava).</summary>
    public void CancelAll()
    {
        List<ClosingJob> active;
        lock (_gate) active = _jobs.Values.Where(j => IsActiveState(j.State)).ToList();
        foreach (var job in active) job.Cts.Cancel();
    }

    public ClosingJob? JobFor(string projectPath)
    {
        lock (_gate) return _jobs.TryGetValue(KeyOf(projectPath), out var job) ? job : null;
    }

    /// <summary>Texto da coluna Fechamento ("Na fila", "Em andamento 42%", "Concluído 03:12", ...). Vazio se nunca fechou.</summary>
    public string ProgressText(string projectPath)
    {
        var job = JobFor(projectPath);
        if (job is null) return "";
        return job.State switch
        {
            ClosingState.Queued => "Na fila",
            ClosingState.Running => $"Em andamento {job.Percent ?? 0}%",
            ClosingState.Done => "Concluído " + ClosingRunner.FormatElapsed((job.FinishedAt ?? DateTime.Now) - (job.StartedAt ?? DateTime.Now)),
            ClosingState.Cancelled => job.Percent is > 0 ? $"Cancelado em {job.Percent}%" : "Cancelado",
            _ => job.Percent is not null ? $"Erro em {job.Percent}%" : "Erro"
        };
    }

    /// <summary>Valida, trava o projeto e coloca na fila. Devolve null se enfileirou, ou o motivo do bloqueio.</summary>
    public string? Enqueue(string projectPath)
    {
        projectPath = projectPath.TrimEnd('\\', '/');
        var cloud = IsCloud(Path.GetFileName(projectPath));
        var title = TitleFor(cloud);
        var key = KeyOf(projectPath);
        var bat = Path.Combine(projectPath, CommandsFolder, BatFileName);

        string? reason;
        lock (_gate)
        {
            reason = _jobs.TryGetValue(key, out var current) && IsActiveState(current.State)
                ? "fechamento ja esta na fila ou em andamento"
                : BlockReason(projectPath, cloud);
        }
        ProjectLock? projectLock = null;
        if (reason is null)
        {
            try { projectLock = new ProjectLock(projectPath, title).Acquire(); }
            catch (ProjectLockedException ex) { reason = "projeto em uso: " + ex.Message; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { reason = "nao foi possivel travar o projeto: " + ex.Message; }
        }
        if (reason is not null)
        {
            AppLog.Warn($"{title} bloqueado ({Path.GetFileName(projectPath)}): {reason}");
            return reason;
        }

        var job = new ClosingJob { Key = key, ProjectPath = projectPath, Title = title, BatPath = bat, Lock = projectLock };
        lock (_gate) _jobs[key] = job;
        AppLog.Info($"{title} na fila: {job.Name}.");
        _ = Task.Run(() => RunJobAsync(job));
        return null;
    }

    /// <summary>Cancela o fechamento: na fila sai na hora; rodando, o BAT e seus filhos sao encerrados. False se nao havia.</summary>
    public bool Cancel(string projectPath)
    {
        var job = JobFor(projectPath);
        if (job is null || !IsActiveState(job.State)) return false;
        job.Cts.Cancel();
        return true;
    }

    private async Task RunJobAsync(ClosingJob job)
    {
        var acquired = false;
        try
        {
            await _slots.WaitAsync(job.Cts.Token).ConfigureAwait(false);
            acquired = true;
            await ExecuteAsync(job).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Complete(job, ClosingState.Cancelled, "cancelado pelo operador");
        }
        catch (Exception ex)
        {
            Complete(job, ClosingState.Error, ex.Message, ex);
        }
        finally
        {
            if (acquired) _slots.Release();
            job.Lock?.Release();
            job.Lock = null;
            Finished?.Invoke(job);
        }
    }

    private async Task ExecuteAsync(ClosingJob job)
    {
        job.StartedAt = DateTime.Now;
        job.State = ClosingState.Running;
        job.Percent = 0;
        job.LogPath = Path.Combine(_logFolder, "fechamentos_bat", $"{job.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        AppLog.Info($"{job.Title} iniciado: {job.Name}; log={job.LogPath}");

        Dictionary<string, long> initial;
        try { initial = ClosingRunner.PendingFileSizes(job.ProjectPath); }
        catch (IOException) { initial = new(); }

        var code = await ClosingRunner.RunBatAsync(job.BatPath, job.LogPath, _timeout, () =>
        {
            var remaining = ClosingRunner.PendingFileSizes(job.ProjectPath).Keys.ToHashSet();
            job.Percent = ClosingRunner.ZippedPercent(initial, remaining);
        }, job.Cts.Token).ConfigureAwait(false);

        var pending = PendingFiles(job.ProjectPath);
        var detail = $"codigo_bat={code}; duracao={ClosingRunner.FormatElapsed(DateTime.Now - job.StartedAt.Value)}; log={job.LogPath}";
        if (pending.Count > 0)
        {
            job.Percent = ClosingRunner.ZippedPercent(initial, pending.Select(n => n.ToLowerInvariant()).ToHashSet());
            var preview = string.Join(", ", pending.Take(5)) + (pending.Count > 5 ? $" +{pending.Count - 5}" : "");
            Complete(job, ClosingState.Error, $"{pending.Count} arquivo(s) nao compactado(s): {preview}; {detail}");
            return;
        }
        Complete(job, ClosingState.Done, detail);
    }

    private static void Complete(ClosingJob job, ClosingState state, string message, Exception? ex = null)
    {
        job.FinishedAt = DateTime.Now;
        job.Message = message;
        job.State = state;
        var line = $"{job.Title} {state switch { ClosingState.Done => "concluido", ClosingState.Cancelled => "cancelado", _ => "com erro" }}: {job.Name}; {message}";
        if (state == ClosingState.Done) AppLog.Info(line); else AppLog.Warn(line, ex);
    }
}
