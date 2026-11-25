using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class BatLogPurgeTests
{
    private static string Log(string folder, string name, DateTime lastWrite)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    [Fact]
    public void Removes_only_bat_logs_older_than_the_retention_and_leaves_other_logs_alone()
    {
        using var dir = new TempFolder();
        var batFolder = Path.Combine(dir.Path, ClosingService.BatLogsFolder);
        Directory.CreateDirectory(batFolder);
        var now = new DateTime(2026, 11, 25, 12, 0, 0);
        var old = Log(batFolder, "projeto_velho.log", now.AddDays(-91));
        var recent = Log(batFolder, "projeto_recente.log", now.AddDays(-89));
        var notLog = Log(batFolder, "anotacao.txt", now.AddDays(-200));
        var appLog = Log(dir.Path, "monitorsvn-20250101.log", now.AddDays(-300));   // fora da pasta de BAT

        var removed = ClosingService.PurgeOldLogs(dir.Path, now: now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(notLog));
        Assert.True(File.Exists(appLog));
    }

    [Fact]
    public void Retention_is_configurable_and_a_locked_file_is_skipped_without_failing()
    {
        using var dir = new TempFolder();
        var batFolder = Path.Combine(dir.Path, ClosingService.BatLogsFolder);
        Directory.CreateDirectory(batFolder);
        var now = new DateTime(2026, 11, 25, 12, 0, 0);
        var locked = Log(batFolder, "em_uso.log", now.AddDays(-10));
        var free = Log(batFolder, "livre.log", now.AddDays(-10));
        Log(batFolder, "novo.log", now.AddDays(-2));

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var removed = ClosingService.PurgeOldLogs(dir.Path, keepDays: 7, now: now);
            Assert.Equal(1, removed);
        }

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void Missing_folder_removes_nothing()
    {
        using var dir = new TempFolder();

        Assert.Equal(0, ClosingService.PurgeOldLogs(dir.Path));
        Assert.Equal(0, ClosingService.PurgeOldLogs(Path.Combine(dir.Path, "nao-existe")));
    }
}
