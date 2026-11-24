using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class CsvExporterTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 15, 0, 0);

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Header_and_one_row_per_entry_in_the_period_newest_first()
    {
        var entries = new[]
        {
            new LogEntry { Revision = "1", Author = "ana", Version = "v1", Status = BuildStatus.Generated, Timestamp = Now.AddDays(-2).AddHours(-6) },
            new LogEntry { Revision = "2", Author = "bia", Version = "v2", Status = BuildStatus.Pending, Timestamp = Now.AddHours(-1) },
            new LogEntry { Revision = "3", Author = "cris", Version = "v3", Status = BuildStatus.Generated, Timestamp = Now.AddDays(-40) }
        };

        var lines = Lines(CsvExporter.Build(entries, Now, 7));

        Assert.Equal(3, lines.Length);                       // cabecalho + 2 (a de 40 dias fica de fora)
        Assert.StartsWith("Revisão;Autor;Versão;", lines[0]);
        Assert.StartsWith("2;bia;v2;23/11/2026;14:00;", lines[1]);
        Assert.StartsWith("1;ana;v1;", lines[2]);
    }

    [Fact]
    public void Duration_comes_from_start_and_end_and_error_detail_only_for_errors()
    {
        var start = Now.AddHours(-3);
        var ok = new LogEntry { Revision = "1", Author = "a", Version = "v", Status = BuildStatus.Generated, Timestamp = Now.AddHours(-4),
                                StartedAt = start, FinishedAt = start.AddMinutes(23), ErrorDetail = "velho" };
        var err = new LogEntry { Revision = "2", Author = "a", Version = "v", Status = BuildStatus.Error, Timestamp = Now.AddHours(-5),
                                 ErrorDetail = "Fatal: F2063", Suspect = "fulano (Rev 9)" };

        var lines = Lines(CsvExporter.Build(new[] { ok, err }, Now, 1));
        var okFields = lines[1].Split(';');
        var errFields = lines[2].Split(';');

        Assert.Equal("23", okFields[7]);
        Assert.Equal("", okFields[9]);                       // Gerado: nao exporta ErrorDetail antigo
        Assert.Equal("", errFields[7]);                      // sem Inicio/Termino
        Assert.Equal("Fatal: F2063", errFields[9]);
        Assert.Equal("fulano (Rev 9)", errFields[10]);
    }

    [Theory]
    [InlineData("simples", "simples")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("diz \"oi\"", "\"diz \"\"oi\"\"\"")]
    [InlineData("linha1\r\nlinha2", "linha1 linha2")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("@x", "'@x")]
    [InlineData(null, "")]
    public void Escapes_fields(string? input, string expected) => Assert.Equal(expected, CsvExporter.Escape(input));

    [Fact]
    public void Save_writes_utf8_with_bom()
    {
        var path = Path.Combine(Path.GetTempPath(), "csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            CsvExporter.Save(path, "Revisão;Autor\r\n");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal("Revisão;Autor\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
        }
        finally { File.Delete(path); }
    }
}
