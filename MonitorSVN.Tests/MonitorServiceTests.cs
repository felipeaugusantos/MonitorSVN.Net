using System.Collections.ObjectModel;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class MonitorServiceTests : IDisposable
{
    private const string Version = "379.48.2.30.74.150";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "msvn-monitor-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static LogEntry Entry(string rev, DateTime ts, BuildStatus status, DateTime? startedAt = null) => new()
    {
        Revision = rev, Author = "ana", Version = Version, Timestamp = ts, Status = status, StartedAt = startedAt
    };

    // ------------------------------------------------------------------ janela de consulta ao SVN

    [Fact]
    public void ScanStart_is_today_on_first_run()
    {
        Assert.Equal(Today, MonitorService.ScanStart(null, Today));
    }

    [Fact]
    public void ScanStart_goes_back_to_yesterday_after_a_late_scan_so_the_last_hour_is_not_lost()
    {
        var lastScan = Today.AddDays(-1).ToDateTime(new TimeOnly(23, 0));
        Assert.Equal(Today.AddDays(-1), MonitorService.ScanStart(lastScan, Today));
    }

    [Fact]
    public void ScanStart_never_goes_further_back_than_yesterday()
    {
        var lastScan = Today.AddDays(-10).ToDateTime(new TimeOnly(9, 0));
        Assert.Equal(Today.AddDays(-1), MonitorService.ScanStart(lastScan, Today));
    }

    // ------------------------------------------------------------------ commits substituidos

    [Fact]
    public void MarkSuperseded_ignores_only_older_pending_commits_and_keeps_real_results()
    {
        var now = DateTime.Now;
        var generated = Entry("10", now.AddHours(-3), BuildStatus.Generated);
        var olderPending = Entry("11", now.AddHours(-2), BuildStatus.Pending);
        var newest = Entry("12", now.AddHours(-1), BuildStatus.Pending);

        MonitorService.MarkSuperseded(new[] { generated, olderPending, newest }, Today);

        Assert.Equal(BuildStatus.Generated, generated.Status);
        Assert.Equal(BuildStatus.Ignored, olderPending.Status);
        Assert.Equal(BuildStatus.Pending, newest.Status);
    }

    [Fact]
    public void MarkSuperseded_spans_midnight()
    {
        var yesterdayNight = Entry("20", Today.AddDays(-1).ToDateTime(new TimeOnly(23, 30)), BuildStatus.Pending);
        var thisMorning = Entry("21", Today.ToDateTime(new TimeOnly(0, 10)), BuildStatus.Pending);

        MonitorService.MarkSuperseded(new[] { yesterdayNight, thisMorning }, Today);

        Assert.Equal(BuildStatus.Ignored, yesterdayNight.Status);
        Assert.Equal(BuildStatus.Pending, thisMorning.Status);
    }

    [Fact]
    public void A_new_commit_waits_while_another_build_of_the_same_version_is_running()
    {
        var now = DateTime.Now;
        var running = Entry("30", now.AddHours(-1), BuildStatus.InProgress, startedAt: now.AddMinutes(-30));
        var waiting = Entry("31", now, BuildStatus.Pending);

        Assert.Same(running, MonitorService.BlockingBuild(new[] { running, waiting }, waiting, now));
    }

    [Fact]
    public void A_stuck_build_stops_blocking_after_the_limit()
    {
        var now = DateTime.Now;
        var stuck = Entry("40", now.AddDays(-1), BuildStatus.InProgress, startedAt: now - MonitorService.StuckAfter - TimeSpan.FromMinutes(1));
        var waiting = Entry("41", now, BuildStatus.Pending);

        Assert.Null(MonitorService.BlockingBuild(new[] { stuck, waiting }, waiting, now));
    }

    // ------------------------------------------------------------------ concluido.txt / erro.txt

    [Fact]
    public void Error_file_is_consumed_so_it_does_not_mark_the_next_build()
    {
        var settings = new AppSettings { GenerationRoot = _root };
        var folder = Path.Combine(_root, Version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MonitorService.ErrorFile), "Fatal: F2613 Unit 'untX' not found.");

        var entry = Entry("50", DateTime.Now, BuildStatus.InProgress, startedAt: DateTime.Now);
        var service = new MonitorService(new ObservableCollection<LogEntry> { entry });

        Assert.True(service.RefreshStatus(settings));
        Assert.Equal(BuildStatus.Error, entry.Status);
        Assert.StartsWith("Fatal", entry.ErrorDetail);
        Assert.False(File.Exists(Path.Combine(folder, MonitorService.ErrorFile)));
    }

    [Fact]
    public void ClearResultFiles_removes_leftovers_from_a_previous_build()
    {
        var settings = new AppSettings { GenerationRoot = _root };
        var folder = Path.Combine(_root, Version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MonitorService.DoneFile), "concluido");
        File.WriteAllText(Path.Combine(folder, MonitorService.ErrorFile), "erro");

        MonitorService.ClearResultFiles(settings, Version);

        Assert.False(File.Exists(Path.Combine(folder, MonitorService.DoneFile)));
        Assert.False(File.Exists(Path.Combine(folder, MonitorService.ErrorFile)));
    }

    [Fact]
    public void ClearResultFiles_accepts_a_missing_folder()
    {
        MonitorService.ClearResultFiles(new AppSettings { GenerationRoot = _root }, "nao-existe");
    }
}

public class ParserRangeTests
{
    private const string Dir = "/Padrao/versoes/";

    private static string Xml(params (string Rev, DateTime Local, string Path, string Kind)[] entries) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><log>" +
        string.Concat(entries.Select(e =>
            $"<logentry revision=\"{e.Rev}\"><author>ana</author>" +
            $"<date>{e.Local.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.000000Z}</date>" +
            $"<paths><path action=\"M\" kind=\"{e.Kind}\">{e.Path}</path></paths></logentry>")) +
        "</log>";

    [Fact]
    public void Parse_with_a_range_includes_yesterday_and_today()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var lateYesterday = today.AddDays(-1).ToDateTime(new TimeOnly(23, 40));
        var twoDaysAgo = today.AddDays(-2).ToDateTime(new TimeOnly(12, 0));
        var xml = Xml(("1", lateYesterday, Dir + "379.1/a.pas", "file"),
                      ("2", twoDaysAgo, Dir + "379.2/a.pas", "file"));

        var changes = SvnLogParser.Parse(xml, Dir, today.AddDays(-1), today, Array.Empty<string>());

        Assert.Single(changes);
        Assert.Equal("379.1", changes[0].Version);
    }

    [Fact]
    public void Parse_ignores_loose_files_in_the_monitored_folder()
    {
        var now = DateTime.Now;
        var xml = Xml(("1", now, Dir + "leiame.txt", "file"),
                      ("2", now, Dir + "379.3", "dir"));

        var changes = SvnLogParser.Parse(xml, Dir, DateOnly.FromDateTime(now), Array.Empty<string>());

        Assert.Single(changes);
        Assert.Equal("379.3", changes[0].Version);
    }
}

public class JobPreparerFixTests
{
    [Theory]
    [InlineData("379.48.2.30.74.150", "379.48.2.30.74.150", true)]
    [InlineData("379.48.2.30.74.150.3", "379.48.2.30.74.150", true)]
    [InlineData("379.48.2.30.74.1500", "379.48.2.30.74.150", false)]
    [InlineData("1379.48.2.30.74.150", "379.48.2.30.74.150", false)]
    [InlineData("379.48.2.30.72.88", "379.48.2.30.72", true)]
    public void ContainsWhole_does_not_match_inside_a_longer_number(string version, string pattern, bool expected)
    {
        Assert.Equal(expected, JobPreparer.ContainsWhole(version, pattern));
    }

    [Fact]
    public void Resolve_falls_back_to_default_rule_for_a_longer_number()
    {
        var r = JobPreparer.Resolve("379.48.2.30.74.1500", AppSettings.DefaultRules());
        Assert.Equal("_74_Modelo", r.TemplateJob);
        Assert.Equal("2.30.74.1500", r.ProductVersion);
    }

    [Fact]
    public void Interrupted_copy_is_detected_and_a_finished_copy_is_ready()
    {
        var root = Path.Combine(Path.GetTempPath(), "msvn-copy-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "modelo");
        var dst = Path.Combine(root, "379.9");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "build.cmd"), "echo releaseversion");
        try
        {
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, JobPreparer.IncompleteMarker), "");
            Assert.False(JobPreparer.FolderReady(dst));

            JobPreparer.CopyTemplateFolder(src, dst, new[] { ("releaseversion", "379.9") });

            Assert.True(JobPreparer.FolderReady(dst));
            Assert.Equal("echo 379.9", File.ReadAllText(Path.Combine(dst, "build.cmd")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class SettingsStoreRecoveryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "msvn-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Corrupted_settings_are_kept_aside_instead_of_being_overwritten()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "settings.json"), "{ \"Directories\": [\"/a/\"");   // truncado

        var store = new SettingsStore(_folder);
        var settings = store.Load();
        store.Save(settings);

        Assert.Single(Directory.GetFiles(_folder, "settings.json.corrompido-*"));
        Assert.NotEmpty(store.LoadWarnings);
    }

    [Fact]
    public void Corrupted_history_is_kept_aside()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "entries.json"), "[ { \"Revision\": ");

        var store = new SettingsStore(_folder);

        Assert.Empty(store.LoadEntries());
        Assert.Single(Directory.GetFiles(_folder, "entries.json.corrompido-*"));
    }

    [Fact]
    public void Save_leaves_no_temporary_file_and_round_trips()
    {
        var store = new SettingsStore(_folder);
        var lastScan = new DateTime(2026, 11, 22, 23, 0, 0);
        store.Save(new AppSettings { Directories = new() { "/a/" }, LastScan = lastScan });

        var loaded = new SettingsStore(_folder).Load();

        Assert.Equal(new[] { "/a/" }, loaded.Directories);
        Assert.Equal(lastScan, loaded.LastScan);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }
}
