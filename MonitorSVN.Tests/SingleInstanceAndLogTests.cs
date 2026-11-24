using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class SingleInstanceTests
{
    // chave unica por teste: nao interfere com o app real nem com outros testes
    private static string Key() => "test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Only_the_first_instance_of_a_user_owns_the_lock()
    {
        var key = Key();
        using var first = new SingleInstance(key);
        using var second = new SingleInstance(key);

        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
    }

    [Fact]
    public void A_different_user_key_is_independent()
    {
        using var a = new SingleInstance(Key());
        using var b = new SingleInstance(Key());

        Assert.True(a.IsFirst);
        Assert.True(b.IsFirst);
    }

    [Fact]
    public void Lock_is_released_when_the_first_instance_closes()
    {
        var key = Key();
        var first = new SingleInstance(key);
        Assert.True(first.IsFirst);
        first.Dispose();

        using var next = new SingleInstance(key);
        Assert.True(next.IsFirst);
    }

    [Fact]
    public void Second_launch_signals_the_first()
    {
        var key = Key();
        using var first = new SingleInstance(key);
        using var signaled = new ManualResetEventSlim(false);
        first.Listen(signaled.Set);

        using var second = new SingleInstance(key);
        second.SignalFirst();

        Assert.True(signaled.Wait(TimeSpan.FromSeconds(5)), "a primeira instancia nao foi avisada");
    }

    [Fact]
    public void Current_user_key_is_stable_and_not_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(SingleInstance.CurrentUserKey()));
        Assert.Equal(SingleInstance.CurrentUserKey(), SingleInstance.CurrentUserKey());
    }
}

[Collection("AppLog")]   // AppLog e estatico: estes testes nao podem rodar em paralelo entre si
public class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msvn-log-" + Guid.NewGuid().ToString("N"));

    public AppLogTests() => AppLog.Configure(_dir);

    public void Dispose()
    {
        AppLog.Configure(null);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Today() => Path.Combine(_dir, $"monitorsvn-{DateTime.Now:yyyyMMdd}.log");

    [Fact]
    public void Writes_timestamped_lines_with_level()
    {
        AppLog.Info("varredura iniciada");
        AppLog.Warn("jenkins lento");
        AppLog.Error("falhou");

        var lines = File.ReadAllLines(Today());
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \[INFO \] varredura iniciada$", lines[0]);
        Assert.Contains("[AVISO] jenkins lento", lines[1]);
        Assert.Contains("[ERRO ] falhou", lines[2]);
    }

    [Fact]
    public void Exception_details_go_to_the_log()
    {
        AppLog.Error("deu ruim", new InvalidOperationException("motivo-especifico"));

        var text = File.ReadAllText(Today());
        Assert.Contains("deu ruim", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("motivo-especifico", text);
    }

    [Fact]
    public void Logging_never_throws_even_when_the_folder_is_unusable()
    {
        // um ARQUIVO no lugar da pasta: CreateDirectory/AppendAllText falham
        var blocker = Path.Combine(Path.GetTempPath(), "msvn-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            AppLog.Configure(Path.Combine(blocker, "logs"));
            var ex = Record.Exception(() => { AppLog.Info("a"); AppLog.Warn("b"); AppLog.Error("c", new Exception("d")); AppLog.Purge(); });
            Assert.Null(ex);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Purge_removes_only_old_log_files()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "monitorsvn-20200101.log");
        var recent = Path.Combine(_dir, "monitorsvn-20991231.log");
        var other = Path.Combine(_dir, "outro-arquivo.txt");
        foreach (var f in new[] { old, recent, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-60));
        File.SetLastWriteTime(other, DateTime.Now.AddDays(-60));

        AppLog.Purge(keepDays: 30);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));   // so apaga monitorsvn-*.log
    }
}
