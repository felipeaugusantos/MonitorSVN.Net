using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class SettingsStoreTests : IDisposable
{
    private const string Secret = "S3nh@-Muito-Secreta";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "msvn-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Password_is_never_written_in_plain_text()
    {
        var store = new SettingsStore(_folder);
        store.Save(new AppSettings { RememberCredentials = true, Password = Secret });

        var file = File.ReadAllText(Path.Combine(_folder, "settings.json"));
        Assert.DoesNotContain(Secret, file);
        Assert.Contains("ProtectedPassword", file);
    }

    [Fact]
    public void Remembered_password_survives_a_reload()
    {
        new SettingsStore(_folder).Save(new AppSettings { RememberCredentials = true, Password = Secret });

        var loaded = new SettingsStore(_folder).Load();

        Assert.Equal(Secret, loaded.Password);
    }

    [Fact]
    public void Password_is_not_stored_when_remember_is_off()
    {
        new SettingsStore(_folder).Save(new AppSettings { RememberCredentials = false, Password = Secret });

        var file = File.ReadAllText(Path.Combine(_folder, "settings.json"));
        var loaded = new SettingsStore(_folder).Load();

        Assert.DoesNotContain(Secret, file);
        Assert.Equal("", loaded.Password);
    }

    [Fact]
    public void Corrupted_protected_password_loads_as_empty_instead_of_crashing()
    {
        var store = new SettingsStore(_folder);
        store.Save(new AppSettings { RememberCredentials = true, Password = Secret });

        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "\"ProtectedPassword\": \"", "\"ProtectedPassword\": \"AAAA"));

        Assert.Equal("", store.Load().Password);
    }
}
