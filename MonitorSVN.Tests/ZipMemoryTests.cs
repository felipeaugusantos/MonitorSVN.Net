using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class ZipMemoryTests
{
    private static readonly DateTime Moment = new(2026, 11, 25, 14, 30, 0);

    [Fact]
    public void First_closing_sets_both_totals_and_the_max_only_goes_up()
    {
        using var dir = new TempFolder();
        var memory = new ZipMemory(dir.Path);

        var first = memory.RecordClosing("379.48.2.30.74.150", 15, Moment);
        var smaller = memory.RecordClosing("379.48.2.30.74.150", 12, Moment);
        var bigger = memory.RecordClosing("379.48.2.30.74.150", 18, Moment);

        Assert.Equal(new ZipRecord(15, 15, "2026-11-25 14:30:00"), first);
        Assert.False(first.BelowMax);
        Assert.Equal(12, smaller.LastZips);
        Assert.Equal(15, smaller.MaxZips);
        Assert.True(smaller.BelowMax);
        Assert.Equal(18, bigger.MaxZips);
        Assert.False(bigger.BelowMax);
    }

    [Fact]
    public void Unknown_folder_has_no_record_and_the_key_ignores_case_and_spaces()
    {
        using var dir = new TempFolder();
        var memory = new ZipMemory(dir.Path);
        Assert.Null(memory.GetRecord("379.48.2.30.74.150"));

        memory.RecordClosing("379.48.2.30.74.150_Cloud", 10);

        Assert.Equal(10, memory.GetRecord(" 379.48.2.30.74.150_CLOUD ")!.LastZips);
    }

    [Fact]
    public void Another_instance_sees_what_was_saved_and_reads_the_python_format()
    {
        using var dir = new TempFolder();
        File.WriteAllText(Path.Combine(dir.Path, ZipMemory.FileName), """
            {
              "379.48.2.30.74.111.172": { "last_zips": 9, "max_zips": 14, "updated_at": "2026-11-20 10:00:00" }
            }
            """);

        var memory = new ZipMemory(dir.Path);
        var record = memory.GetRecord("379.48.2.30.74.111.172")!;
        memory.RecordClosing("outra", 3);

        Assert.True(record.BelowMax);
        Assert.Equal(14, record.MaxZips);
        var again = new ZipMemory(dir.Path);
        Assert.Equal(3, again.GetRecord("outra")!.LastZips);
        Assert.Equal(14, again.GetRecord("379.48.2.30.74.111.172")!.MaxZips);   // o que ja existia foi mantido
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Corrupted_file_is_kept_aside_and_the_memory_restarts()
    {
        using var dir = new TempFolder();
        File.WriteAllText(Path.Combine(dir.Path, ZipMemory.FileName), "{ nao e json");
        var memory = new ZipMemory(dir.Path);

        Assert.Null(memory.GetRecord("x"));
        Assert.Equal(4, memory.RecordClosing("x", 4).MaxZips);
        Assert.True(File.Exists(memory.FilePath + ".corrompido.bak"));
    }

    [Fact]
    public void Counts_only_zip_files_in_the_root()
    {
        using var dir = new TempFolder();
        File.WriteAllText(Path.Combine(dir.Path, "a.zip"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "B.ZIP"), "x");
        File.WriteAllText(Path.Combine(dir.Path, "c.exe"), "x");
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllText(Path.Combine(dir.Path, "sub", "d.zip"), "x");

        Assert.Equal(2, ZipMemory.CountZipFiles(dir.Path));
    }

    [Fact]
    public async Task Closing_records_the_zip_total_and_flags_a_drop_below_the_max()
    {
        using var dir = new TempFolder();
        var memory = new ZipMemory(Path.Combine(dir.Path, "dados"));
        const string name = "379.48.2.30.74.150";
        memory.RecordClosing(name, 5);   // ja gerou 5 ZIPs antes

        var project = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(Path.Combine(project, "comandosCMD"));
        File.WriteAllText(Path.Combine(project, "comandosCMD", ClosingService.BatFileName),
            "@echo off\r\ndel /q \"%~dp0..\\*.exe\"\r\necho x > \"%~dp0..\\autcom.zip\"\r\nexit /b 1\r\n");
        File.WriteAllText(Path.Combine(project, "autcom.exe"), "x");
        var service = new ClosingService(dir.Path, zips: memory);

        Assert.Null(service.Enqueue(project));
        for (var i = 0; i < 400 && service.IsActive(project); i++) await Task.Delay(50);

        var job = service.JobFor(project)!;
        Assert.Equal(ClosingState.Done, job.State);
        Assert.Equal(new ZipRecord(1, 5, job.ZipRecord!.UpdatedAt), job.ZipRecord);
        Assert.True(job.ZipRecord.BelowMax);
        Assert.Contains("zips=1; maior_registrado=5", job.Message);
        Assert.True(memory.GetRecord(name)!.BelowMax);
    }

    [Fact]
    public void Entry_shows_the_alert_in_the_tooltip_only_when_there_is_a_hint()
    {
        var entry = new LogEntry { Revision = "1", Version = "V", Status = BuildStatus.Generated };
        Assert.False(entry.ZipBelowMax);
        Assert.DoesNotContain("ZIP", entry.ToolTipText);

        entry.ZipHint = "Atenção: o último fechamento gerou 1 ZIP(s), mas esta pasta já gerou 5.";

        Assert.True(entry.ZipBelowMax);
        Assert.Contains("já gerou 5", entry.ToolTipText);
    }
}
