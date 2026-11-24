using System.Text.Json;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class VarreduraNotifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "msvn-notif-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppSettings Settings() => new() { GenerationRoot = _root };

    private static LogEntry Entry(BuildStatus status, string? error = null) => new()
    {
        Revision = "104233",
        Author = "felipe",
        Version = "379.48.2.30.74.150",
        Timestamp = new DateTime(2026, 11, 19, 14, 32, 0),
        Status = status,
        ErrorDetail = error
    };

    private string[] Lines() => File.ReadAllLines(Path.Combine(_root, VarreduraNotifier.FileName));

    [Fact]
    public void Generated_writes_one_json_line_with_version_and_origin()
    {
        VarreduraNotifier.Notify(Settings(), Entry(BuildStatus.Generated));

        var line = Assert.Single(Lines());
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        Assert.Equal("379.48.2.30.74.150", r.GetProperty("versao").GetString());
        Assert.Equal("104233", r.GetProperty("revisao").GetString());
        Assert.Equal("felipe", r.GetProperty("autor").GetString());
        Assert.Equal("gerado", r.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("detalhe").ValueKind);
    }

    [Fact]
    public void Error_includes_detail_and_events_are_appended()
    {
        VarreduraNotifier.Notify(Settings(), Entry(BuildStatus.Generated));
        VarreduraNotifier.Notify(Settings(), Entry(BuildStatus.Error, "Fatal: acentuação ok"));

        var lines = Lines();
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[1]);
        Assert.Equal("erro", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("Fatal: acentuação ok", doc.RootElement.GetProperty("detalhe").GetString());
    }

    [Theory]
    [InlineData(BuildStatus.Pending)]
    [InlineData(BuildStatus.InProgress)]
    [InlineData(BuildStatus.Ignored)]
    public void Other_statuses_write_nothing(BuildStatus status)
    {
        VarreduraNotifier.Notify(Settings(), Entry(status));

        Assert.False(File.Exists(Path.Combine(_root, VarreduraNotifier.FileName)));
    }

    [Fact]
    public void Unwritable_root_does_not_throw()
    {
        // um arquivo no lugar da pasta raiz faz CreateDirectory/abertura falhar
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "arquivo");
        File.WriteAllText(blocker, "x");

        var ex = Record.Exception(() => VarreduraNotifier.Notify(new AppSettings { GenerationRoot = blocker }, Entry(BuildStatus.Generated)));

        Assert.Null(ex);
    }
}
