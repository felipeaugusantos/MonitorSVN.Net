using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class ClosingRunnerTests
{
    [Fact]
    public void Pending_sizes_only_count_exe_dll_bpl_in_the_root()
    {
        using var dir = new TempFolder();
        File.WriteAllBytes(Path.Combine(dir.Path, "Autcom.EXE"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir.Path, "lib.dll"), new byte[50]);
        File.WriteAllBytes(Path.Combine(dir.Path, "pkg.bpl"), new byte[10]);
        File.WriteAllBytes(Path.Combine(dir.Path, "autcom.zip"), new byte[999]);
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllBytes(Path.Combine(dir.Path, "sub", "x.exe"), new byte[7]);

        var sizes = ClosingRunner.PendingFileSizes(dir.Path);

        Assert.Equal(3, sizes.Count);
        Assert.Equal(100, sizes["autcom.exe"]);
    }

    [Fact]
    public void Percent_is_weighted_by_size_and_capped_at_99()
    {
        var initial = new Dictionary<string, long> { ["a.exe"] = 750, ["b.dll"] = 250 };

        Assert.Equal(0, ClosingRunner.ZippedPercent(initial, new[] { "a.exe", "b.dll" }));
        Assert.Equal(75, ClosingRunner.ZippedPercent(initial, new[] { "b.dll" }));
        Assert.Equal(99, ClosingRunner.ZippedPercent(initial, Array.Empty<string>()));
        Assert.Equal(0, ClosingRunner.ZippedPercent(new Dictionary<string, long>(), Array.Empty<string>()));
    }

    [Theory]
    [InlineData(5, "00:05")]
    [InlineData(125, "02:05")]
    [InlineData(3725, "1:02:05")]
    public void Elapsed_format(int seconds, string expected) =>
        Assert.Equal(expected, ClosingRunner.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task Runs_the_bat_writes_the_log_and_returns_the_exit_code()
    {
        using var dir = new TempFolder();
        var bat = Path.Combine(dir.Path, "ok.bat");
        File.WriteAllText(bat, "@echo off\r\necho ola fechamento\r\nexit /b 3\r\n");
        var log = Path.Combine(dir.Path, "logs", "saida.log");

        var code = await ClosingRunner.RunBatAsync(bat, log, TimeSpan.FromSeconds(30), null, CancellationToken.None);

        Assert.Equal(3, code);
        Assert.Contains("ola fechamento", File.ReadAllText(log));
    }
}

public class ClosingServiceTests
{
    private static string Slow => "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n";

    private static string MakeProject(string root, string name, string batContent, params string[] files)
    {
        var project = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(project, "comandosCMD"));
        File.WriteAllText(Path.Combine(project, "comandosCMD", ClosingService.BatFileName), batContent);
        foreach (var f in files) File.WriteAllBytes(Path.Combine(project, f), new byte[10]);
        return project;
    }

    private static async Task WaitUntilFinishedAsync(ClosingService service, string project)
    {
        for (var i = 0; i < 400 && service.IsActive(project); i++) await Task.Delay(50);
        Assert.False(service.IsActive(project), "o fechamento nao terminou a tempo");
    }

    [Fact]
    public async Task Closing_is_done_when_no_exe_dll_bpl_is_left_even_if_the_bat_exits_with_1()
    {
        using var dir = new TempFolder();
        var project = MakeProject(dir.Path, "379.48.2.30.74.150",
            "@echo off\r\ndel /q \"%~dp0..\\*.exe\" \"%~dp0..\\*.dll\"\r\nexit /b 1\r\n", "autcom.exe", "lib.dll");
        var service = new ClosingService(dir.Path);

        Assert.Null(service.Enqueue(project));
        await WaitUntilFinishedAsync(service, project);

        var job = service.JobFor(project)!;
        Assert.Equal(ClosingState.Done, job.State);
        Assert.StartsWith("Concluído ", service.ProgressText(project));
        Assert.Null(ProjectLock.ReadOwner(project));   // trava solta
        Assert.True(File.Exists(job.LogPath));
    }

    [Fact]
    public async Task Leftover_executables_make_the_closing_fail_and_are_listed()
    {
        using var dir = new TempFolder();
        var project = MakeProject(dir.Path, "379.48.2.30.74.150", "@echo off\r\nexit /b 0\r\n", "autcom.exe", "lib.dll");
        var service = new ClosingService(dir.Path);

        Assert.Null(service.Enqueue(project));
        await WaitUntilFinishedAsync(service, project);

        var job = service.JobFor(project)!;
        Assert.Equal(ClosingState.Error, job.State);
        Assert.Contains("2 arquivo(s) nao compactado(s): autcom.exe, lib.dll", job.Message);
        Assert.StartsWith("Erro", service.ProgressText(project));
        Assert.Null(ProjectLock.ReadOwner(project));
    }

    [Fact]
    public async Task Cancel_stops_a_running_bat_and_releases_the_lock()
    {
        using var dir = new TempFolder();
        var project = MakeProject(dir.Path, "379.48.2.30.74.150", Slow, "autcom.exe");
        var service = new ClosingService(dir.Path);
        Assert.Null(service.Enqueue(project));
        await Task.Delay(1500);
        Assert.True(service.IsActive(project));
        Assert.NotNull(ProjectLock.ReadOwner(project));

        Assert.True(service.Cancel(project));
        await WaitUntilFinishedAsync(service, project);

        Assert.Equal(ClosingState.Cancelled, service.JobFor(project)!.State);
        Assert.Null(ProjectLock.ReadOwner(project));
        Assert.False(service.Cancel(project));
    }

    [Fact]
    public async Task Timeout_ends_the_bat_as_an_error()
    {
        using var dir = new TempFolder();
        var project = MakeProject(dir.Path, "379.48.2.30.74.150", Slow, "autcom.exe");
        var service = new ClosingService(dir.Path, timeout: TimeSpan.FromSeconds(2));

        Assert.Null(service.Enqueue(project));
        await WaitUntilFinishedAsync(service, project);

        var job = service.JobFor(project)!;
        Assert.Equal(ClosingState.Error, job.State);
        Assert.Contains("excedeu", job.Message);
    }

    [Fact]
    public async Task Only_the_configured_number_run_at_once_and_the_rest_wait_in_the_queue()
    {
        using var dir = new TempFolder();
        var a = MakeProject(dir.Path, "379.48.2.30.74.1", Slow, "a.exe");
        var b = MakeProject(dir.Path, "379.48.2.30.74.2", Slow, "b.exe");
        var service = new ClosingService(dir.Path, maxConcurrent: 1);
        Assert.Null(service.Enqueue(a));
        Assert.Null(service.Enqueue(b));
        await Task.Delay(1500);

        Assert.Equal(ClosingState.Running, service.JobFor(a)!.State);
        Assert.Equal("Na fila", service.ProgressText(b));

        // cancelar o que esta na fila: sai na hora e solta a trava dele
        Assert.True(service.Cancel(b));
        await WaitUntilFinishedAsync(service, b);
        Assert.Equal(ClosingState.Cancelled, service.JobFor(b)!.State);
        Assert.Null(ProjectLock.ReadOwner(b));

        service.Cancel(a);
        await WaitUntilFinishedAsync(service, a);
    }

    [Fact]
    public void Blocks_a_mismatched_type_a_missing_bat_and_a_bad_zip_name()
    {
        using var dir = new TempFolder();
        var local = MakeProject(dir.Path, "379.48.2.30.74.150", "@echo off\r\n");
        var cloud = MakeProject(dir.Path, "379.48.2.30.74.150_CLOUD", "@echo off\r\n");
        var noBat = Path.Combine(dir.Path, "sem-bat");
        Directory.CreateDirectory(Path.Combine(noBat, "comandosCMD"));

        Assert.Equal("projeto CLOUD nao pode usar fechamento local", ClosingService.BlockReason(cloud, expectCloud: false));
        Assert.Equal("projeto local nao pode usar fechamento cloud", ClosingService.BlockReason(local, expectCloud: true));
        Assert.Null(ClosingService.BlockReason(local, expectCloud: false));
        Assert.Null(ClosingService.BlockReason(cloud, expectCloud: true));
        Assert.StartsWith("BAT nao encontrado", ClosingService.BlockReason(noBat, expectCloud: false));
        Assert.StartsWith("pasta nao encontrada", ClosingService.BlockReason(Path.Combine(dir.Path, "nao-existe"), false));

        System.IO.Compression.ZipFile.Open(Path.Combine(local, "errado.zip"), System.IO.Compression.ZipArchiveMode.Create)
            .Dispose();
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(local, "errado.zip"), System.IO.Compression.ZipArchiveMode.Update))
            zip.CreateEntry("autcom.exe");
        Assert.StartsWith("1 ZIP(s) com nome incorreto", ClosingService.BlockReason(local, expectCloud: false));
    }

    [Fact]
    public void Enqueue_is_refused_while_another_operator_holds_the_project()
    {
        using var dir = new TempFolder();
        var project = MakeProject(dir.Path, "379.48.2.30.74.150", "@echo off\r\n", "autcom.exe");
        using var other = new ProjectLock(project, "Copiar Local").Acquire();
        var service = new ClosingService(dir.Path);

        var reason = service.Enqueue(project);

        Assert.StartsWith("projeto em uso: Copiar Local em andamento", reason);
        Assert.Null(service.JobFor(project));
    }

    [Fact]
    public async Task Cannot_enqueue_the_same_project_twice()
    {
        using var dir = new TempFolder();
        var project = MakeProject(dir.Path, "379.48.2.30.74.150", Slow, "autcom.exe");
        var service = new ClosingService(dir.Path);
        Assert.Null(service.Enqueue(project));

        Assert.Equal("fechamento ja esta na fila ou em andamento", service.Enqueue(project));

        service.Cancel(project);
        await WaitUntilFinishedAsync(service, project);
    }
}
