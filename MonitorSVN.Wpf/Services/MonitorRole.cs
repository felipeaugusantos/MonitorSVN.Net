using System.Text;
using System.Text.Json;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>
/// Quem monitora o SVN/Jenkins quando ha mais de um operador no servidor (cada usuario do Windows tem a sua
/// instancia e o seu historico). So uma instancia por vez e o "monitor": ela consulta o SVN, dispara builds e
/// avisa. As outras mostram o historico em somente leitura. O papel e uma trava em rede
/// (<c>monitor_svn.lock</c> na pasta de controle) com renovacao automatica: se o monitor fechar ou perder a
/// rede, a trava expira e outra instancia assume.
/// Pasta de controle vazia = recurso desligado (esta instancia sempre monitora, como antes).
/// </summary>
public sealed class MonitorRole : IDisposable
{
    public const string LockFileName = "monitor_svn.lock";
    public const string Action = "Monitoramento SVN";

    private readonly string _folder;
    private readonly TimeSpan? _heartbeat;
    private readonly TimeSpan? _staleAfter;
    private ProjectLock? _lock;

    public MonitorRole(string? controlFolder, TimeSpan? heartbeat = null, TimeSpan? staleAfter = null)
    {
        _folder = controlFolder?.Trim() ?? "";
        _heartbeat = heartbeat;
        _staleAfter = staleAfter;
        IsMonitor = !Enabled;
    }

    public bool Enabled => _folder.Length > 0;
    public string Folder => _folder;

    /// <summary>Esta instancia pode consultar o SVN e disparar builds.</summary>
    public bool IsMonitor { get; private set; }

    /// <summary>Texto para o operador: quem monitora, ou por que esta instancia nao monitora.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Tenta assumir (ou confirma que ainda tem) o papel de monitor. Acessa a rede: nao chamar na thread da tela.</summary>
    public bool Evaluate()
    {
        if (!Enabled) return IsMonitor = true;

        if (_lock is not null)
        {
            if (_lock.StillOwns()) return IsMonitor = true;
            _lock.Release();   // perdeu a trava (rede fora do ar por mais que o prazo): outro pode ter assumido
            _lock = null;
        }

        IsMonitor = false;
        try
        {
            if (!Directory.Exists(_folder))
            {
                Status = $"Pasta de controle indisponível ({_folder}). Somente leitura.";
                return false;
            }
            _lock = new ProjectLock(_folder, Action, _heartbeat, _staleAfter, LockFileName).Acquire();
            Status = "";
            return IsMonitor = true;
        }
        catch (ProjectLockedException ex)
        {
            Status = $"Monitoramento ativo com {(string.IsNullOrEmpty(ex.Owner.User) ? "outro operador" : ex.Owner.User)}. Somente leitura.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Nao foi possivel usar a pasta de controle {_folder}.", ex);
            Status = $"Pasta de controle indisponível ({ex.Message}). Somente leitura.";
            return false;
        }
    }

    /// <summary>Solta o papel ao fechar o app, para outro operador assumir na hora (sem esperar a trava expirar).</summary>
    public void Release()
    {
        _lock?.Release();
        _lock = null;
        if (Enabled) IsMonitor = false;
    }

    public void Dispose() => Release();
}

/// <summary>
/// Historico e marca da ultima varredura que o monitor publica na pasta de controle, para quem assumir depois
/// continuar de onde ele parou (sem disparar de novo builds que o outro ja fez) e para as outras instancias
/// mostrarem a mesma lista.
/// </summary>
public static class MonitorSharedState
{
    public const string FileName = "monitor_svn_estado.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class State
    {
        public DateTime? LastScan { get; set; }
        public List<LogEntry> Entries { get; set; } = new();
    }

    public static string PathFor(string folder) => Path.Combine(folder, FileName);

    public static void Save(string folder, IEnumerable<LogEntry> entries, DateTime? lastScan)
    {
        var path = PathFor(folder);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(new State { LastScan = lastScan, Entries = entries.ToList() }, Json);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <returns>null se ainda nao existe, ou se nao pode ser lido agora (tenta de novo na proxima vez).</returns>
    public static State? TryLoad(string folder)
    {
        try
        {
            var path = PathFor(folder);
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn("Nao foi possivel ler o estado compartilhado do monitoramento.", ex);
            return null;
        }
    }

    public static DateTime? LastWriteUtc(string folder)
    {
        try
        {
            var path = PathFor(folder);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
