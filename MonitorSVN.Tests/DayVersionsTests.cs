using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class DayVersionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dayver-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 11, 23, 15, 0, 0);

    public DayVersionsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private AppSettings Settings() => new() { GenerationRoot = _root };

    private LogEntry E(string version, BuildStatus status, int dayOffset = 0, bool dismissed = false) =>
        new() { Revision = "1", Version = version, Status = status, Timestamp = _now.AddDays(dayOffset), Dismissed = dismissed };

    private void Folder(string version, bool incomplete = false)
    {
        var dir = Path.Combine(_root, version);
        Directory.CreateDirectory(dir);
        if (incomplete) File.WriteAllText(Path.Combine(dir, JobPreparer.IncompleteMarker), "x");
    }

    [Fact]
    public void Only_todays_versions_with_a_ready_folder_are_included()
    {
        Folder("A"); Folder("B"); Folder("OLD"); Folder("HALF", incomplete: true);
        var entries = new[]
        {
            E("A", BuildStatus.Generated), E("B", BuildStatus.Error), E("C", BuildStatus.Pending),   // C: sem pasta
            E("OLD", BuildStatus.Generated, dayOffset: -1), E("HALF", BuildStatus.Generated),
            E("A", BuildStatus.Ignored)                                                               // mesma versao: uma vez so
        };

        var (versions, skipped) = TemplateLibService.DayVersions(entries, Settings(), _now);

        Assert.Equal(new[] { "A", "B" }, versions);
        Assert.Empty(skipped);
    }

    [Fact]
    public void Version_with_a_build_in_progress_is_skipped_with_a_reason()
    {
        Folder("A"); Folder("B");
        var entries = new[] { E("A", BuildStatus.InProgress), E("A", BuildStatus.Ignored), E("B", BuildStatus.Generated) };

        var (versions, skipped) = TemplateLibService.DayVersions(entries, Settings(), _now);

        Assert.Equal(new[] { "B" }, versions);
        Assert.Contains("[A]", Assert.Single(skipped));
    }

    [Fact]
    public void Dismissed_entries_do_not_count()
    {
        Folder("A");
        var (versions, _) = TemplateLibService.DayVersions(new[] { E("A", BuildStatus.Error, dismissed: true) }, Settings(), _now);
        Assert.Empty(versions);
    }
}
