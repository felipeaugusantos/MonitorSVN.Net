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

    public AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            s = new AppSettings();
        }

        if (s.Rules.Count == 0) s.Rules = AppSettings.DefaultRules();
        if (s.RememberCredentials) s.Password = Unprotect(s.ProtectedPassword);
        return s;
    }

    public void Save(AppSettings s)
    {
        s.ProtectedPassword = s.RememberCredentials ? Protect(s.Password) : "";
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Json));
    }

    public List<LogEntry> LoadEntries()
    {
        try
        {
            return File.Exists(EntriesPath)
                ? JsonSerializer.Deserialize<List<LogEntry>>(File.ReadAllText(EntriesPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    public void SaveEntries(IEnumerable<LogEntry> entries) =>
        File.WriteAllText(EntriesPath, JsonSerializer.Serialize(entries, Json));

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
