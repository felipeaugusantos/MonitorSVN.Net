using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class ParserTests
{
    private const string Dir = "/Padrao/027.18/27.18.379/379.48.02.REV.FECHADAs/379.48.02.30.FECHADAS/";

    // datas em UTC; o parser converte para o horario local
    private static string Xml(string utcDate) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <log>
          <logentry revision="48231">
            <author>ana.souza</author>
            <date>{utcDate}</date>
            <paths>
              <path action="M" kind="file">{Dir}379.48.2.30.74.150/src/untMain.pas</path>
              <path action="M" kind="file">{Dir}379.48.2.30.74.150/src/untFuncoes.pas</path>
              <path action="A" kind="dir">{Dir}379.48.2.30.74.152</path>
            </paths>
          </logentry>
          <logentry revision="48230">
            <author>carla.dias</author>
            <date>{utcDate}</date>
            <paths>
              <path action="A" kind="dir" copyfrom-path="/outro/caminho" copyfrom-rev="1">{Dir}379.48.2.30.72.88</path>
              <path action="M" kind="file">/Outro/Lugar/arquivo.pas</path>
            </paths>
          </logentry>
        </log>
        """;

    private static (string Utc, DateOnly Day) Now()
    {
        var local = DateTime.Now;
        return (local.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.000000Z"), DateOnly.FromDateTime(local));
    }

    [Fact]
    public void Parse_returns_one_change_per_version_and_skips_copies_and_other_paths()
    {
        var (utc, day) = Now();
        var changes = SvnLogParser.Parse(Xml(utc), Dir, day, Array.Empty<string>());

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Revision == "48231" && c.Version == "379.48.2.30.74.150" && c.Author == "ana.souza");
        Assert.Contains(changes, c => c.Revision == "48231" && c.Version == "379.48.2.30.74.152");
        Assert.DoesNotContain(changes, c => c.Version == "379.48.2.30.72.88"); // copia (copyfrom)
    }

    [Fact]
    public void Parse_ignores_configured_users_case_insensitively()
    {
        var (utc, day) = Now();
        var changes = SvnLogParser.Parse(Xml(utc), Dir, day, new[] { "ANA.SOUZA" });
        Assert.Empty(changes);
    }

    [Fact]
    public void Parse_skips_commits_from_other_days()
    {
        var (utc, day) = Now();
        var changes = SvnLogParser.Parse(Xml(utc), Dir, day.AddDays(-1), Array.Empty<string>());
        Assert.Empty(changes);
    }

    [Fact]
    public void Parse_handles_empty_output()
    {
        Assert.Empty(SvnLogParser.Parse("", Dir, DateOnly.FromDateTime(DateTime.Now), Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Padrao/x", "svn://srv", "/Padrao/x/")]
    [InlineData("svn://srv/Padrao/x/", "svn://srv", "/Padrao/x/")]
    [InlineData("/Padrao/x", "svn://srv", "/Padrao/x/")]
    public void NormalizeDir_adds_slashes_and_strips_base_url(string input, string baseUrl, string expected)
    {
        Assert.Equal(expected, SvnLogParser.NormalizeDir(input, baseUrl));
    }
}

public class RuleTests
{
    private static readonly List<VersionRule> Rules = AppSettings.DefaultRules();

    [Theory]
    [InlineData("379.48.2.30.72.88",    "_72_Modelo",     "48.2.30.72", "2.30.72.88")]
    [InlineData("379.48.2.30.74.74",    "_74_74_Modelo",  "48.2.30.74", "2.30.74.74")]
    [InlineData("379.48.2.30.74.111",   "_74_111_Modelo", "48.2.30.74", "2.30.74.111")]
    [InlineData("379.48.2.30.74.150",   "_74_Modelo",     "48.2.30.74", "2.30.74.150")]
    [InlineData("379.48.2.30.74.152",   "_74_Modelo",     "48.2.30.74", "2.30.74.152")] // regra padrao
    public void Resolve_matches_the_rules_of_the_delphi_app(string version, string template, string file, string product)
    {
        var r = JobPreparer.Resolve(version, Rules);
        Assert.Equal(template, r.TemplateJob);
        Assert.Equal(file, r.FileVersion);
        Assert.Equal(product, r.ProductVersion);
    }

    [Fact]
    public void CopyDirectory_replaces_markers_only_in_text_scripts()
    {
        var root = Path.Combine(Path.GetTempPath(), "monitorsvn-test-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "build.bat"), "set V=releaseversion FILE=fileversion PROD=productversion", System.Text.Encoding.Latin1);
        File.WriteAllText(Path.Combine(src, "sub", "nota.txt"), "versao RELEASEVERSION", System.Text.Encoding.Latin1);
        File.WriteAllText(Path.Combine(src, "dados.xml"), "releaseversion", System.Text.Encoding.Latin1);

        try
        {
            JobPreparer.CopyDirectory(src, dst, new[] { ("releaseversion", "R1"), ("fileversion", "F1"), ("productversion", "P1") });

            Assert.Equal("set V=R1 FILE=F1 PROD=P1", File.ReadAllText(Path.Combine(dst, "build.bat")));
            Assert.Equal("versao R1", File.ReadAllText(Path.Combine(dst, "sub", "nota.txt")));
            Assert.Equal("releaseversion", File.ReadAllText(Path.Combine(dst, "dados.xml"))); // .xml nao e alterado
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
