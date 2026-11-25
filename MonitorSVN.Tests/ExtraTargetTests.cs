using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class ExtraTargetTests
{
    private static readonly string[] Defaults = CopyRules.DefaultTargetRoots;

    [Theory]
    [InlineData(@"\\srv\Modem\VERSOES", @"\\srv\Modem\VERSOES")]
    [InlineData(@"  \\srv\Modem\VERSOES\  ", @"\\srv\Modem\VERSOES")]
    [InlineData("\"D:\\Versoes\\Copias\"", @"D:\Versoes\Copias")]
    [InlineData(@"D:/Versoes/", @"D:/Versoes")]
    public void Accepts_full_paths_and_cleans_quotes_spaces_and_trailing_slashes(string typed, string expected)
    {
        var (value, error) = CopyRules.NormalizeExtraTarget(typed, Defaults);

        Assert.Equal(expected, value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pasta-relativa")]
    [InlineData(@"..\acima")]
    [InlineData(@"\\")]
    [InlineData(@"D:\")]
    [InlineData("D:")]
    public void Rejects_empty_relative_and_disk_root_paths(string typed)
    {
        var (value, error) = CopyRules.NormalizeExtraTarget(typed, Defaults);

        Assert.Null(value);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Rejects_duplicates_of_the_list_and_of_the_default_targets_ignoring_case_and_trailing_slash()
    {
        var existing = Defaults.Concat(new[] { @"\\srv\Modem\Extra" });

        Assert.Null(CopyRules.NormalizeExtraTarget(@"\\SRV\modem\extra\", existing).Value);
        Assert.Null(CopyRules.NormalizeExtraTarget(CopyRules.IndividualRoot.ToLowerInvariant() + @"\", existing).Value);
        Assert.NotNull(CopyRules.NormalizeExtraTarget(@"\\srv\Modem\Outro", existing).Value);
    }
}
