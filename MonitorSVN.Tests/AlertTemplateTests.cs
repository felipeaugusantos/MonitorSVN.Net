using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class AlertTemplateTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 15, 0, 0);
    private static readonly AppSettings Settings = new() { JenkinsUrl = "http://jenkins.exemplo/" };

    private static BuildAlert Error(string? suspect = "ana (Rev 9, uA.pas)") => new(AlertKind.Error,
        new LogEntry
        {
            Version = "379.48.2.30.74.9", Revision = "507447", Author = "bia", Status = BuildStatus.Error, Suspect = suspect,
            StartedAt = Now.AddMinutes(-23), FinishedAt = Now.AddMinutes(-2)
        }, "");

    private static BuildAlert Slow() => new(AlertKind.Slow,
        new LogEntry { Version = "v1", Revision = "10", Author = "bia", Status = BuildStatus.InProgress, StartedAt = Now.AddMinutes(-90) }, "");

    [Fact]
    public void Fields_are_replaced_for_an_error()
    {
        var text = AlertTemplate.Line("{versao}|{revisao}|{autor}|{tipo}|{situacao}|{responsavel}|{tempo}|{hora}", Error(), Settings, Now);

        Assert.Equal("379.48.2.30.74.9|507447|bia|erro|terminou com erro|ana (Rev 9, uA.pas)|21 min|14:58", text);
    }

    [Fact]
    public void Fields_are_replaced_for_a_slow_build()
    {
        var text = AlertTemplate.Line("{versao} {tipo}: {situacao} ({tempo})", Slow(), Settings, Now);

        Assert.Equal("v1 demorando: está em andamento há 1 h 30 min (1 h 30 min)", text);
    }

    [Fact]
    public void Link_points_to_the_jenkins_job()
    {
        Assert.Equal("http://jenkins.exemplo/job/379.48.2.30.74.9/", AlertTemplate.Line("{link}", Error(), Settings, Now));
        Assert.Equal("", AlertTemplate.Line("{link}", Error(), new AppSettings { JenkinsUrl = "lixo" }, Now));
    }

    [Fact]
    public void Optional_part_disappears_when_its_field_is_empty()
    {
        const string t = "{versao} falhou.[ Responsável: {responsavel}.]";

        Assert.Equal("379.48.2.30.74.9 falhou. Responsável: ana (Rev 9, uA.pas).", AlertTemplate.Line(t, Error(), Settings, Now));
        Assert.Equal("379.48.2.30.74.9 falhou.", AlertTemplate.Line(t, Error(suspect: null), Settings, Now));
        Assert.Equal("379.48.2.30.74.9 falhou.", AlertTemplate.Line(t, Error(suspect: ""), Settings, Now));
        Assert.Equal("v1 falhou.", AlertTemplate.Line(t, Slow(), Settings, Now));   // build demorando nunca tem responsavel
    }

    [Fact]
    public void Empty_template_uses_the_default_which_matches_the_old_text()
    {
        var text = AlertTemplate.Line("", Error(suspect: null), Settings, Now);

        Assert.Equal("379.48.2.30.74.9 terminou com erro (Rev 507447, bia).", text);
        Assert.Equal(AlertTemplate.DefaultLine, AlertTemplate.Or("  "));
    }

    [Fact]
    public void Default_adds_the_suspect_when_known()
    {
        var text = AlertTemplate.Line(null, Error(), Settings, Now);

        Assert.Equal("379.48.2.30.74.9 terminou com erro (Rev 507447, bia). Provável responsável: ana (Rev 9, uA.pas).", text);
    }

    [Fact]
    public void Unknown_field_is_found_and_left_visible()
    {
        Assert.Equal("versoa", AlertTemplate.UnknownField("{autor} {versoa}"));
        Assert.Null(AlertTemplate.UnknownField("{AUTOR} {versao}"));
        Assert.Null(AlertTemplate.UnknownField(null));
        Assert.Equal("{versoa}", AlertTemplate.Line("{versoa}", Error(), Settings, Now));
    }

    [Fact]
    public void Chat_and_email_use_their_own_templates()
    {
        var settings = new AppSettings { ChatLineTemplate = "CHAT {versao}", EmailLineTemplate = "MAIL {autor}" };
        var alerts = new List<BuildAlert> { Error() };

        Assert.Contains("• CHAT 379.48.2.30.74.9", ChatNotifier.Compose(alerts, settings, Now));
        Assert.Contains("• MAIL bia", EmailNotifier.Compose(alerts, settings, Now).Body);
    }

    [Fact]
    public void Preview_never_throws_and_fills_every_field()
    {
        var all = string.Join(" ", AlertTemplate.Fields.Select(f => "{" + f + "}"));

        var text = AlertTemplate.Preview(all, Settings);

        Assert.DoesNotContain("{", text);
        Assert.Contains("felipe.santos", text);
    }

    [Fact]
    public void Success_has_its_own_wording_and_build_duration()
    {
        var alert = new BuildAlert(AlertKind.Success, new LogEntry
        {
            Version = "v9", Revision = "5", Author = "bia", Status = BuildStatus.Generated,
            StartedAt = Now.AddMinutes(-30), FinishedAt = Now.AddMinutes(-4)
        }, "");

        Assert.Equal("v9 gerada: foi gerada com sucesso em 26 min às 14:56",
            AlertTemplate.Line("{versao} {tipo}: {situacao} em {tempo} às {hora}", alert, Settings, Now));
        Assert.Equal("v9 foi gerada com sucesso (Rev 5, bia).", AlertTemplate.Line(null, alert, Settings, Now));
    }
}
