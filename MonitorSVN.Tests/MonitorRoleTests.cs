using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class MonitorRoleTests
{
    [Fact]
    public void Disabled_when_no_folder_the_instance_always_monitors()
    {
        using var role = new MonitorRole("");

        Assert.True(role.IsMonitor);
        Assert.True(role.Evaluate());
        Assert.False(role.Enabled);
    }

    [Fact]
    public void Only_one_instance_monitors_and_the_other_reports_who()
    {
        using var dir = new TempFolder();
        using var first = new MonitorRole(dir.Path);
        using var second = new MonitorRole(dir.Path);

        Assert.True(first.Evaluate());
        Assert.False(second.Evaluate());

        Assert.False(second.IsMonitor);
        Assert.Contains(Environment.UserName, second.Status);
        Assert.Contains("Somente leitura", second.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Releasing_lets_the_other_instance_take_over_at_once()
    {
        using var dir = new TempFolder();
        using var first = new MonitorRole(dir.Path);
        using var second = new MonitorRole(dir.Path);
        first.Evaluate();
        Assert.False(second.Evaluate());

        first.Release();

        Assert.True(second.Evaluate());
    }

    [Fact]
    public void Expired_lock_from_a_crashed_instance_is_taken_over()
    {
        using var dir = new TempFolder();
        var file = ProjectLock.PathFor(dir.Path, MonitorRole.LockFileName);
        File.WriteAllText(file, """{"user":"matheus.marcomin","action":"Monitoramento SVN","token":"x"}""");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - ProjectLock.DefaultStaleAfter - TimeSpan.FromSeconds(5));

        using var role = new MonitorRole(dir.Path);

        Assert.True(role.Evaluate());
    }

    [Fact]
    public void Instance_that_lost_its_lock_stops_monitoring()
    {
        using var dir = new TempFolder();
        using var role = new MonitorRole(dir.Path);
        Assert.True(role.Evaluate());
        // outra instancia assumiu (a nossa trava havia expirado por falha de rede)
        File.WriteAllText(ProjectLock.PathFor(dir.Path, MonitorRole.LockFileName),
            """{"user":"matheus.marcomin","action":"Monitoramento SVN","token":"alheio"}""");

        Assert.False(role.Evaluate());
        Assert.Contains("matheus.marcomin", role.Status);
    }

    [Fact]
    public void Missing_control_folder_is_read_only_not_monitoring()
    {
        using var role = new MonitorRole(Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N")));

        Assert.False(role.Evaluate());
        Assert.Contains("indisponível", role.Status);
    }

    [Fact]
    public void Role_lock_does_not_collide_with_the_project_lock_file()
    {
        using var dir = new TempFolder();
        using var role = new MonitorRole(dir.Path);
        role.Evaluate();

        Assert.Null(ProjectLock.ReadOwner(dir.Path));   // .varredura.lock da pasta nao existe
        Assert.NotNull(ProjectLock.ReadOwner(dir.Path, fileName: MonitorRole.LockFileName));
    }
}

public class MonitorSharedStateTests
{
    [Fact]
    public void Round_trip_keeps_entries_and_last_scan()
    {
        using var dir = new TempFolder();
        var scan = new DateTime(2026, 11, 25, 14, 0, 0);
        var entries = new[]
        {
            new LogEntry { Revision = "123", Author = "felipe.santos", Version = "379.48.2.30.74.150",
                           Timestamp = new DateTime(2026, 11, 25, 9, 30, 0), Status = BuildStatus.Generated }
        };

        MonitorSharedState.Save(dir.Path, entries, scan);
        var state = MonitorSharedState.TryLoad(dir.Path)!;

        Assert.Equal(scan, state.LastScan);
        var loaded = Assert.Single(state.Entries);
        Assert.Equal("123", loaded.Revision);
        Assert.Equal(BuildStatus.Generated, loaded.Status);
        Assert.False(File.Exists(MonitorSharedState.PathFor(dir.Path) + ".tmp"));
    }

    [Fact]
    public void Missing_or_corrupted_state_returns_null()
    {
        using var dir = new TempFolder();
        Assert.Null(MonitorSharedState.TryLoad(dir.Path));

        File.WriteAllText(MonitorSharedState.PathFor(dir.Path), "{ nao e json");
        Assert.Null(MonitorSharedState.TryLoad(dir.Path));
    }
}
