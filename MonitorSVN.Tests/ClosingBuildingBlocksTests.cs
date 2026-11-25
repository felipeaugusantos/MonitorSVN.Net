using System.IO.Compression;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "monitorsvn-test-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ProjectLockTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(10);

    [Fact]
    public void Second_acquire_is_blocked_with_owner_details()
    {
        using var dir = new TempFolder();
        using var first = new ProjectLock(dir.Path, "Fechamento Local").Acquire();

        var ex = Assert.Throws<ProjectLockedException>(() => new ProjectLock(dir.Path, "Copiar Local").Acquire());

        Assert.Equal("Fechamento Local", ex.Owner.Action);
        Assert.Equal(Environment.UserName, ex.Owner.User);
        Assert.Contains("Fechamento Local em andamento por", ex.Message);
    }

    [Fact]
    public void Release_frees_the_project()
    {
        using var dir = new TempFolder();
        new ProjectLock(dir.Path, "A").Acquire().Release();

        Assert.Null(ProjectLock.ReadOwner(dir.Path));
        using var second = new ProjectLock(dir.Path, "B").Acquire();
        Assert.Equal("B", ProjectLock.ReadOwner(dir.Path)!.Action);
    }

    [Fact]
    public void Stale_lock_from_a_closed_app_is_taken_over()
    {
        using var dir = new TempFolder();
        var file = ProjectLock.PathFor(dir.Path);
        File.WriteAllText(file, """{"user":"joao.silva","action":"Copiar Local","token":"x"}""");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - ProjectLock.DefaultStaleAfter - TimeSpan.FromSeconds(5));

        Assert.Null(ProjectLock.ReadOwner(dir.Path));
        using var mine = new ProjectLock(dir.Path, "Fechamento Cloud").Acquire();
        Assert.Equal("Fechamento Cloud", ProjectLock.ReadOwner(dir.Path)!.Action);
    }

    [Fact]
    public void Release_does_not_remove_a_lock_taken_over_by_someone_else()
    {
        using var dir = new TempFolder();
        var mine = new ProjectLock(dir.Path, "A").Acquire();
        File.WriteAllText(ProjectLock.PathFor(dir.Path), """{"user":"outro","action":"B","token":"alheio"}""");

        mine.Release();

        Assert.True(File.Exists(ProjectLock.PathFor(dir.Path)));
    }

    [Fact]
    public async Task Heartbeat_keeps_a_long_operation_locked()
    {
        using var dir = new TempFolder();
        using var mine = new ProjectLock(dir.Path, "A", heartbeat: TimeSpan.FromMilliseconds(50), staleAfter: Slow).Acquire();
        var file = ProjectLock.PathFor(dir.Path);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromHours(1));

        await Task.Delay(400);

        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(1));
        Assert.NotNull(ProjectLock.ReadOwner(dir.Path));
    }

    [Fact]
    public void Reads_a_lock_written_by_the_python_system()
    {
        using var dir = new TempFolder();
        File.WriteAllText(ProjectLock.PathFor(dir.Path),
            """{"user": "matheus.marcomin", "host": "TS01", "pid": 4321, "action": "Copiar Local", "started": "25/11/2026 14:30", "token": "abc"}""");

        var owner = ProjectLock.ReadOwner(dir.Path)!;

        Assert.Equal("matheus.marcomin", owner.User);
        Assert.Equal("Copiar Local em andamento por matheus.marcomin desde 25/11/2026 14:30", owner.Describe());
    }

    [Fact]
    public void Half_written_lock_counts_as_in_use()
    {
        using var dir = new TempFolder();
        File.WriteAllText(ProjectLock.PathFor(dir.Path), "{");

        Assert.NotNull(ProjectLock.ReadOwner(dir.Path));
    }
}

public class ZipNameValidatorTests
{
    private static void MakeZip(string folder, string zipName, params string[] entries)
    {
        using var archive = ZipFile.Open(Path.Combine(folder, zipName), ZipArchiveMode.Create);
        foreach (var name in entries) archive.CreateEntry(name);
    }

    [Fact]
    public void Matching_names_pass_case_insensitively()
    {
        using var dir = new TempFolder();
        MakeZip(dir.Path, "Autcom.zip", "autcom.exe");
        MakeZip(dir.Path, "libFuncoes.zip", "libfuncoes.dll");

        Assert.Empty(ZipNameValidator.Validate(dir.Path));
    }

    [Fact]
    public void Wrong_zip_name_is_reported()
    {
        using var dir = new TempFolder();
        MakeZip(dir.Path, "autcom_novo.zip", "autcom.exe");

        var errors = ZipNameValidator.Validate(dir.Path);

        Assert.Equal(new[] { "autcom_novo.zip contem autcom.exe; esperado autcom.zip" }, errors);
    }

    [Fact]
    public void Corrupted_zip_is_reported()
    {
        using var dir = new TempFolder();
        File.WriteAllText(Path.Combine(dir.Path, "quebrado.zip"), "isto nao e um zip");

        Assert.Equal(new[] { "quebrado.zip: ZIP invalido ou corrompido" }, ZipNameValidator.Validate(dir.Path));
    }

    [Fact]
    public void Err_files_and_other_extensions_are_ignored_and_several_executables_are_not_checked()
    {
        using var dir = new TempFolder();
        MakeZip(dir.Path, "autcom.zip", "autcom.exe", "err_autcom.exe", "leia-me.txt");
        MakeZip(dir.Path, "pacote.zip", "a.exe", "b.dll");
        MakeZip(dir.Path, "docs.zip", "leia-me.txt");

        Assert.Empty(ZipNameValidator.Validate(dir.Path));
    }

    [Fact]
    public void Entry_inside_a_subfolder_uses_only_the_file_name()
    {
        using var dir = new TempFolder();
        MakeZip(dir.Path, "autcom.zip", "bin/autcom.exe");

        Assert.Empty(ZipNameValidator.Validate(dir.Path));
    }
}

public class VersionReaderTests
{
    [Theory]
    [InlineData(48, 2, 30, 74, "48.02.30.74")]
    [InlineData(2, 30, 74, 5, "02.30.74.05")]
    [InlineData(379, 48, 10, 0, "379.48.10.00")]
    public void Format_pads_parts_below_ten(int a, int b, int c, int d, string expected) =>
        Assert.Equal(expected, VersionReader.Format(a, b, c, d));

    [Fact]
    public void Reads_the_version_of_a_real_dll()
    {
        var versions = VersionReader.Read(typeof(object).Assembly.Location);

        Assert.NotNull(versions);
        Assert.Equal(4, versions!.Value.File.Split('.').Length);
        Assert.Equal(4, versions.Value.Product.Split('.').Length);
    }

    [Fact]
    public void File_without_version_resource_returns_null()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "sem-versao.dll");
        File.WriteAllText(path, "nada");

        Assert.Null(VersionReader.Read(path));
    }
}
