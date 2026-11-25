using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>Le/grava configuracoes e o historico em %APPDATA%\MonitorSVN (nao mais na pasta do executavel).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _folder;

    public SettingsStore(string? folder = null)
    {
        // MONITORSVN_DATA permite apontar outra pasta de dados (testes, execucao portatil)
        _folder = folder
                  ?? Environment.GetEnvironmentVariable("MONITORSVN_DATA")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSVN");
        Directory.CreateDirectory(_folder);
    }

    private string SettingsPath => Path.Combine(_folder, "settings.json");
    private string EntriesPath => Path.Combine(_folder, "entries.json");

    /// <summary>Problemas encontrados ao carregar (ex.: arquivo corrompido guardado à parte), para avisar o usuario.</summary>
    public List<string> LoadWarnings { get; } = new();

    public AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException ex)
        {
            Quarantine(SettingsPath, "Configurações", ex);
            s = new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Nao foi possivel ler {SettingsPath}.", ex);
            LoadWarnings.Add($"Não foi possível ler as configurações ({ex.Message}). Usando os valores padrão.");
            s = new AppSettings();
        }

        if (s.Rules.Count == 0) s.Rules = AppSettings.DefaultRules();
        if (s.RememberCredentials) s.Password = Unprotect(s.ProtectedPassword);
        s.SmtpPassword = Unprotect(s.ProtectedSmtpPassword);
        s.ChatWebhook = Unprotect(s.ProtectedChatWebhook);
        return s;
    }

    public void Save(AppSettings s)
    {
        s.ProtectedSmtpPassword = Protect(s.SmtpPassword);
        s.ProtectedChatWebhook = Protect(s.ChatWebhook);
        s.ProtectedPassword = s.RememberCredentials ? Protect(s.Password) : "";
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(s, Json));
    }

    public List<LogEntry> LoadEntries()
    {
        try
        {
            return File.Exists(EntriesPath)
                ? JsonSerializer.Deserialize<List<LogEntry>>(File.ReadAllText(EntriesPath)) ?? new()
                : new();
        }
        catch (JsonException ex)
        {
            Quarantine(EntriesPath, "Histórico", ex);
            return new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Nao foi possivel ler {EntriesPath}.", ex);
            LoadWarnings.Add($"Não foi possível ler o histórico ({ex.Message}).");
            return new();
        }
    }

    public void SaveEntries(IEnumerable<LogEntry> entries) =>
        WriteAtomic(EntriesPath, JsonSerializer.Serialize(entries, Json));

    /// <summary>
    /// Grava num .tmp (forcando a ida ao disco) e so entao troca pelo arquivo final. Se o processo cair ou
    /// faltar energia no meio, o arquivo anterior continua inteiro.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Guarda o arquivo ilegivel ao lado (para nao ser sobrescrito pelos padroes) e avisa.</summary>
    private void Quarantine(string path, string what, Exception ex)
    {
        var copy = $"{path}.corrompido-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, copy);
            AppLog.Error($"{path} corrompido; guardado como {copy}.", ex);
            LoadWarnings.Add($"{what}: arquivo corrompido. Foi guardado como {copy} e os valores padrão foram carregados.");
        }
        catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"{path} corrompido e nao foi possivel guarda-lo a parte.", moveEx);
            LoadWarnings.Add($"{what}: arquivo corrompido ({path}).");
        }
    }

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    private static string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
