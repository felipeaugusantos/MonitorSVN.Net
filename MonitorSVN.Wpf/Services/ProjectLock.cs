using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorSVN.Services;

/// <summary>Quem segura a trava de um projeto. Os nomes do JSON sao os mesmos do VarreduraSistema (Python).</summary>
public sealed class LockOwner
{
    [JsonPropertyName("user")] public string User { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("pid")] public int Pid { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("started")] public string Started { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";

    /// <summary>"usuario/acao em andamento por X desde Y" para mensagens ao operador.</summary>
    public string Describe()
    {
        var user = string.IsNullOrEmpty(User) ? "outro operador" : User;
        var action = string.IsNullOrEmpty(Action) ? "uso" : Action;
        var text = $"{action} em andamento por {user}";
        return string.IsNullOrEmpty(Started) ? text : $"{text} desde {Started}";
    }
}

public sealed class ProjectLockedException : Exception
{
    public LockOwner Owner { get; }
    public ProjectLockedException(LockOwner owner) : base(owner.Describe()) => Owner = owner;
}

/// <summary>
/// Trava por projeto compartilhada entre operadores (e entre este app e o VarreduraSistema): um arquivo
/// ".varredura.lock" na pasta do projeto, criado de forma atomica (CreateNew). Quem segura renova a data do
/// arquivo a cada <see cref="HeartbeatInterval"/>; se o app fechar sem soltar, a trava expira apos
/// <see cref="StaleAfter"/> sem renovacao. Formato e nome identicos aos do sistema em Python, para os dois
/// se respeitarem enquanto coexistirem.
/// </summary>
public sealed class ProjectLock : IDisposable
{
    public const string FileName = ".varredura.lock";
    public static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(180);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly string _action;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly TimeSpan _heartbeat;
    private readonly TimeSpan _staleAfter;
    private CancellationTokenSource? _stop;
    private bool _held;

    /// <param name="fileName">Outro nome de arquivo (ex.: trava do monitoramento); o padrao e o do VarreduraSistema.</param>
    public ProjectLock(string projectPath, string action, TimeSpan? heartbeat = null, TimeSpan? staleAfter = null,
        string fileName = FileName)
    {
        _path = PathFor(projectPath, fileName);
        _action = action;
        _heartbeat = heartbeat ?? DefaultHeartbeat;
        _staleAfter = staleAfter ?? DefaultStaleAfter;
    }

    public static string PathFor(string projectPath, string fileName = FileName) => Path.Combine(projectPath, fileName);

    /// <summary>Dono atual da trava, ou null se nao houver trava valida (ausente ou expirada).</summary>
    public static LockOwner? ReadOwner(string projectPath, TimeSpan? staleAfter = null, string fileName = FileName)
    {
        var path = PathFor(projectPath, fileName);
        try
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (!File.Exists(path)) return null;
            var owner = JsonSerializer.Deserialize<LockOwner>(File.ReadAllText(path), Json);
            if (age > (staleAfter ?? DefaultStaleAfter)) return null;
            return owner;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // arquivo sendo criado por outro neste instante: trata como em uso
            return new LockOwner { User = "outro operador", Action = "Uso" };
        }
    }

    /// <exception cref="ProjectLockedException">Outro operador (ou processo) segura a trava.</exception>
    public ProjectLock Acquire()
    {
        var owner = new LockOwner
        {
            User = Environment.UserName,
            Host = Environment.MachineName,
            Pid = Environment.ProcessId,
            Action = _action,
            Started = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
            Token = _token
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(_path))
            {
                var current = ReadOwner(Path.GetDirectoryName(_path)!, _staleAfter, Path.GetFileName(_path));
                if (current is not null) throw new ProjectLockedException(current);
                // expirada (app fechado sem soltar): remove e tenta de novo
                try { File.Delete(_path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                continue;
            }

            using (stream)
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(owner, Json));

            _held = true;
            StartHeartbeat();
            return this;
        }
        throw new ProjectLockedException(ReadOwner(Path.GetDirectoryName(_path)!, _staleAfter, Path.GetFileName(_path)) ?? new LockOwner());
    }

    private void StartHeartbeat()
    {
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(_heartbeat, token);
                    File.SetLastWriteTimeUtc(_path, DateTime.UtcNow);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException) { }
        });
    }

    /// <summary>A trava ainda e nossa? Falso se expirou (rede fora do ar) e outro a assumiu, ou se o arquivo sumiu.</summary>
    public bool StillOwns()
    {
        if (!_held) return false;
        try { return JsonSerializer.Deserialize<LockOwner>(File.ReadAllText(_path), Json)?.Token == _token; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Solta a trava. So apaga o arquivo se ele ainda for nosso (pode ter expirado e sido assumido por outro).</summary>
    public void Release()
    {
        if (!_held) return;
        _held = false;
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        try
        {
            var owner = JsonSerializer.Deserialize<LockOwner>(File.ReadAllText(_path), Json);
            if (owner?.Token == _token) File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Dispose() => Release();
}
