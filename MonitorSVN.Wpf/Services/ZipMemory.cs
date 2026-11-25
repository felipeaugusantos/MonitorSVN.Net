using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorSVN.Services;

/// <summary>Total de ZIPs do ultimo fechamento de uma pasta e o maior total ja visto nela.</summary>
public sealed record ZipRecord(int LastZips, int MaxZips, string UpdatedAt = "")
{
    /// <summary>O ultimo fechamento gerou menos ZIPs que o maior ja registrado: pode estar faltando arquivo.</summary>
    public bool BelowMax => LastZips < MaxZips;
}

/// <summary>
/// Memoria de ZIPs por pasta (zip_memory.json), porta do zip_memory.py do VarreduraSistema, no mesmo formato:
/// <c>{ "nome-da-pasta": { "last_zips": 12, "max_zips": 15, "updated_at": "2026-11-25 14:30:00" } }</c>.
/// Com pasta de controle fica compartilhada entre os operadores. Atencao: o maior total nunca diminui sozinho; se uma
/// versao passar a ter menos ZIPs de proposito, o aviso so some ao editar/remover a entrada no arquivo.
/// </summary>
public sealed class ZipMemory
{
    public const string FileName = "zip_memory.json";
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class Entry
    {
        [JsonPropertyName("last_zips")] public int LastZips { get; set; }
        [JsonPropertyName("max_zips")] public int MaxZips { get; set; }
        [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    }

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, Entry>? _cache;
    private DateTime _checkedAt = DateTime.MinValue;
    private DateTime? _loadedStamp;

    public ZipMemory(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, FileName);
    }

    public string FilePath => _path;

    private static string Key(string folderName) => folderName.Trim().ToLowerInvariant();

    public static int CountZipFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly).Count();

    /// <summary>Registro da pasta, ou null se nunca foi fechada. Consulta barata: relê o arquivo so se ele mudou (a cada 5 s).</summary>
    public ZipRecord? GetRecord(string folderName)
    {
        lock (_gate)
        {
            var data = Load(force: false);
            return data.TryGetValue(Key(folderName), out var e) ? new ZipRecord(e.LastZips, e.MaxZips, e.UpdatedAt) : null;
        }
    }

    /// <summary>Registra o total do fechamento e devolve o registro atualizado (o maior total so sobe).</summary>
    public ZipRecord RecordClosing(string folderName, int zipCount, DateTime? now = null)
    {
        // outro operador pode gravar junto (arquivo na rede): trava curta entre processos
        using var guard = AcquireWriteLock();
        lock (_gate)
        {
            var data = new Dictionary<string, Entry>(Load(force: true));
            var key = Key(folderName);
            var max = data.TryGetValue(key, out var previous) ? Math.Max(zipCount, previous.MaxZips) : zipCount;
            var entry = new Entry { LastZips = zipCount, MaxZips = max, UpdatedAt = (now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm:ss") };
            data[key] = entry;

            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            finally { try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

            _cache = data;
            _loadedStamp = Stamp();
            _checkedAt = DateTime.UtcNow;
            return new ZipRecord(entry.LastZips, entry.MaxZips, entry.UpdatedAt);
        }
    }

    private DateTime? Stamp()
    {
        try { return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private Dictionary<string, Entry> Load(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && _cache is not null && now - _checkedAt < RecheckEvery) return _cache;
        _checkedAt = now;

        var stamp = Stamp();
        if (!force && _cache is not null && stamp == _loadedStamp) return _cache;

        try
        {
            _cache = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path, Encoding.UTF8)) ?? new()
                : new();
        }
        catch (JsonException ex)
        {
            AppLog.Warn($"{_path} corrompido; copia guardada ao lado.", ex);
            try { File.Copy(_path, _path + ".corrompido.bak", overwrite: true); } catch (Exception io) when (io is IOException or UnauthorizedAccessException) { }
            _cache = new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Nao foi possivel ler {_path}.", ex);
            _cache ??= new();
        }
        _loadedStamp = stamp;
        return _cache;
    }

    private ProjectLock? AcquireWriteLock()
    {
        var folder = Path.GetDirectoryName(_path)!;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { return new ProjectLock(folder, "Gravar memoria de ZIPs", fileName: "zip_memory.lock").Acquire(); }
            catch (ProjectLockedException) { Thread.Sleep(250); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
        }
        return null;
    }
}
