using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class ErrorBlameTests
{
    [Theory]
    [InlineData("Fatal: F2063 Could not compile used unit 'untGarantiaEnvFornecedor.pas'", "untGarantiaEnvFornecedor.pas")]
    [InlineData("Fatal: F2063 Could not compile used unit 'untGarantia'", "untGarantia.pas")]
    [InlineData("Error: E2003 em C:\\x\\untFoo.pas(10)", "untFoo.pas")]
    public void Extracts_file_from_error(string text, string expected) =>
        Assert.Equal(expected, ErrorBlame.ExtractFile(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Erro generico sem arquivo")]
    public void No_file_returns_null(string? text) => Assert.Null(ErrorBlame.ExtractFile(text));

    private const string Xml = """
        <log><logentry revision="507386"><author>fulano</author><paths>
        <path action="M">/Padrao/x/untGarantiaEnvFornecedor.pas</path>
        <path action="M">/Padrao/x/outro.pas</path></paths></logentry></log>
        """;

    [Fact]
    public void Touches_matches_file_name_case_insensitive() =>
        Assert.True(ErrorBlame.Touches(Xml, "untgarantiaenvfornecedor.PAS"));

    [Fact]
    public void Touches_requires_whole_file_name()
    {
        Assert.False(ErrorBlame.Touches(Xml, "Fornecedor.pas"));
        Assert.False(ErrorBlame.Touches("", "a.pas"));
    }
}
