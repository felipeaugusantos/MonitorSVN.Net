using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class AutoChainTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 15, 0, 0);

    private static LogEntry Entry(string version, BuildStatus status, DateTime? finished, bool autoClosed = false, bool dismissed = false) =>
        new() { Revision = "1", Version = version, Status = status, FinishedAt = finished, AutoClosed = autoClosed, Dismissed = dismissed };

    [Fact]
    public void Picks_only_fresh_generated_lines_not_yet_tried()
    {
        var fresh = Entry("A", BuildStatus.Generated, Now.AddMinutes(-5));
        var entries = new[]
        {
            fresh,
            Entry("B", BuildStatus.Generated, Now.AddHours(-3)),                       // antiga: nao fecha o historico
            Entry("C", BuildStatus.Generated, Now.AddMinutes(-5), autoClosed: true),   // ja tentado
            Entry("D", BuildStatus.Generated, Now.AddMinutes(-5), dismissed: true),    // removido pelo usuario
            Entry("E", BuildStatus.Error, Now.AddMinutes(-5)),
            Entry("F", BuildStatus.InProgress, null),
            Entry("G", BuildStatus.Generated, null)
        };

        Assert.Equal(new[] { fresh }, AutoChain.SelectForClosing(entries, Now));
    }

    [Fact]
    public void Older_finish_goes_first()
    {
        var late = Entry("A", BuildStatus.Generated, Now.AddMinutes(-1));
        var early = Entry("B", BuildStatus.Generated, Now.AddMinutes(-9));

        Assert.Equal(new[] { early, late }, AutoChain.SelectForClosing(new[] { late, early }, Now));
    }
}
