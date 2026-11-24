using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class LogSearchTests
{
    private static LogEntry E(string rev, string author, string version) =>
        new() { Revision = rev, Author = author, Version = version };

    private static readonly LogEntry Felipe = E("507109", "felipe.santos", "379.48.2.30.72.461");
    private static readonly LogEntry Renan = E("507150", "renan", "379.48.2.30.74.150.100");
    private static readonly LogEntry Danilo = E("507151", "danilo.stefaneli", "379.48.2.30.74.150.45.69");

    private static List<string> Find(string query) =>
        new[] { Felipe, Renan, Danilo }.Where(new LogSearch(query).Matches).Select(e => e.Author).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Rev:")]    // so o prefixo, sem nada para procurar
    public void Empty_query_is_not_active(string? query) => Assert.False(new LogSearch(query).IsActive);

    [Fact]
    public void Matches_by_revision()
    {
        Assert.Equal(new[] { "felipe.santos" }, Find("507109"));
        Assert.Equal(new[] { "felipe.santos", "renan", "danilo.stefaneli" }, Find("5071"));   // prefixo comum a todas
    }

    [Theory]
    [InlineData("r507109")]
    [InlineData("R507109")]
    [InlineData("Rev: 507109")]
    [InlineData("rev:507109")]
    [InlineData("REV: R507109")]
    public void Revision_prefixes_are_accepted(string query) =>
        Assert.Equal(new[] { "felipe.santos" }, Find(query));

    [Theory]
    [InlineData("felipe", "felipe.santos")]
    [InlineData("SANTOS", "felipe.santos")]      // sem diferenciar maiusculas
    [InlineData("renan", "renan")]
    [InlineData("stefa", "danilo.stefaneli")]
    public void Matches_by_author(string query, string expected) => Assert.Equal(new[] { expected }, Find(query));

    [Theory]
    [InlineData("72.461", "felipe.santos")]
    [InlineData("150.100", "renan")]
    [InlineData("45.69", "danilo.stefaneli")]
    public void Matches_by_version(string query, string expected) => Assert.Equal(new[] { expected }, Find(query));

    [Fact]
    public void Several_terms_must_all_match_in_any_field()
    {
        Assert.Equal(new[] { "renan" }, Find("renan 150.100"));
        Assert.Equal(new[] { "renan" }, Find("150.100 renan"));          // ordem nao importa
        Assert.Empty(Find("felipe 150.100"));                              // autor de um, versao de outro
        Assert.Equal(new[] { "renan", "danilo.stefaneli" }, Find("74.150 5071"));   // o Felipe e da 72, fica de fora
    }

    [Fact]
    public void A_revision_prefix_term_does_not_match_the_author_or_version()
    {
        // "r461" procura so revisoes: a versao 72.461 nao conta
        Assert.Empty(Find("r461"));
        // sem o "r", 461 casa com a versao do Felipe
        Assert.Equal(new[] { "felipe.santos" }, Find("461"));
    }

    [Fact]
    public void No_match_returns_nothing()
    {
        Assert.Empty(Find("naoexiste"));
    }
}
