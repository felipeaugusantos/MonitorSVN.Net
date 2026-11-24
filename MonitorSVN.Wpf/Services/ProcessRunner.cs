using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MonitorSVN.Services;

/// <summary>
/// Executa um programa sem passar pelo cmd.exe (argumentos separados, sem injecao de comando),
/// sem travar a interface e com tempo limite.
/// </summary>
public static class ProcessRunner
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Nao foi possivel iniciar '{fileName}'.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Nao foi possivel executar '{fileName}'. Ele esta instalado e no PATH? ({ex.Message})", ex);
        }

        using (process)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                await Task.WhenAll(output, error);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"Tempo esgotado ({timeout.TotalSeconds:0}s) ao executar '{fileName}'.");
            }

            return (process.ExitCode, await output, await error);
        }
    }
}
