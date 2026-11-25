using System.Diagnostics;

namespace MonitorSVN.Services;

/// <summary>
/// Execucao do _FechamentoArquivos.bat e calculo do andamento. Porta do closing_runner.py do VarreduraSistema.
/// O BAT compacta cada .exe/.dll/.bpl da raiz da pasta num .zip e apaga o original; o fechamento termina
/// quando nao resta nenhum deles.
/// </summary>
public static class ClosingRunner
{
    public static readonly string[] PendingExtensions = { ".exe", ".dll", ".bpl" };
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>Tamanho (por nome, minusculo) dos .exe/.dll/.bpl ainda nao compactados na raiz da pasta.</summary>
    public static Dictionary<string, long> PendingFileSizes(string folder)
    {
        var sizes = new Dictionary<string, long>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
        {
            if (!PendingExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
            try { sizes[Path.GetFileName(path).ToLowerInvariant()] = new FileInfo(path).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // sumiu agora: ja foi compactado
        }
        return sizes;
    }

    /// <summary>
    /// Percentual ja compactado, ponderado pelo tamanho inicial de cada arquivo. No maximo 99 enquanto o BAT roda:
    /// 100 so vale com o fechamento concluido.
    /// </summary>
    public static int ZippedPercent(IReadOnlyDictionary<string, long> initialSizes, IReadOnlyCollection<string> remainingNames)
    {
        var total = initialSizes.Values.Sum();
        if (total <= 0) return 0;
        var remaining = initialSizes.Where(kv => remainingNames.Contains(kv.Key)).Sum(kv => kv.Value);
        return Math.Min(99, (int)((total - remaining) * 100 / total));
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = (int)Math.Max(0, elapsed.TotalSeconds);
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        seconds %= 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes:00}:{seconds:00}";
    }

    /// <summary>
    /// Executa o BAT em console oculto, gravando a saida no log, e devolve o codigo de saida. Chama
    /// <paramref name="onTick"/> a cada segundo (andamento). Se cancelar, ou estourar o tempo, encerra o BAT e tudo
    /// que ele abriu.
    /// O BAT precisa de um console proprio (comandos como "timeout" falham sem ele): por isso o inicio e feito pelo
    /// shell, com a janela oculta, e o redirecionamento para o log e feito pelo proprio cmd.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelado pelo operador.</exception>
    /// <exception cref="TimeoutException">O BAT passou do tempo limite.</exception>
    public static async Task<int> RunBatAsync(string batPath, string logPath, TimeSpan timeout,
        Action? onTick, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/s /c \"\"{batPath}\" > \"{logPath}\" 2>&1\"",
            WorkingDirectory = Path.GetDirectoryName(batPath)!,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Nao foi possivel iniciar o BAT.");
        // prioridade baixa: mantem o servidor responsivo (os compactadores herdam)
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        var exited = process.WaitForExitAsync(CancellationToken.None);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (ct.IsCancellationRequested) { Kill(process); throw new OperationCanceledException(ct); }
            if (deadline.Elapsed >= timeout)
            {
                Kill(process);
                throw new TimeoutException($"O BAT excedeu {FormatElapsed(timeout)} e foi encerrado.");
            }

            var step = TimeSpan.FromMilliseconds(Math.Min(TickInterval.TotalMilliseconds, (timeout - deadline.Elapsed).TotalMilliseconds));
            var finished = await Task.WhenAny(exited, Task.Delay(step, ct)).ConfigureAwait(false);
            if (finished == exited) return process.ExitCode;

            if (ct.IsCancellationRequested) continue;   // trata no topo do laco
            try { onTick?.Invoke(); } catch (Exception) { }   // andamento e so informativo
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); process.WaitForExit(10_000); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
