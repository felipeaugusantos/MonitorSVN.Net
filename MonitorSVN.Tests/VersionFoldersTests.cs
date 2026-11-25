using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class VersionFoldersTests
{
    [Theory]
    [InlineData("379.48.2.30.74.150", "379.48.2.30.74.150")]
    [InlineData("379.48.2.30.74.150_CLOUD", "379.48.2.30.74.150")]
    [InlineData("379.48.2.30.74.150_cloud", "379.48.2.30.74.150")]
    public void Base_name_drops_the_cloud_suffix(string version, string expected) =>
        Assert.Equal(expected, VersionFolders.BaseName(version));

    [Fact]
    public void Each_version_has_a_local_and_a_cloud_sibling_folder()
    {
        Assert.Equal(Path.Combine("C:", "ROOT", "379.48.2.30.74.150"), VersionFolders.PathFor(Path.Combine("C:", "ROOT"), "379.48.2.30.74.150", cloud: false));
        Assert.Equal(Path.Combine("C:", "ROOT", "379.48.2.30.74.150_CLOUD"), VersionFolders.PathFor(Path.Combine("C:", "ROOT"), "379.48.2.30.74.150", cloud: true));
        // se a linha ja trouxer o sufixo, as duas pastas continuam sendo as mesmas
        Assert.Equal(VersionFolders.FolderName("379.48.2.30.74.150", false), VersionFolders.FolderName("379.48.2.30.74.150_CLOUD", false));
        Assert.Equal("379.48.2.30.74.150_CLOUD", VersionFolders.FolderName("379.48.2.30.74.150_CLOUD", true));
    }

    [Fact]
    public void Folder_names_match_the_closing_type_rule()
    {
        Assert.False(ClosingService.IsCloud(VersionFolders.FolderName("379.48.2.30.74.150", cloud: false)));
        Assert.True(ClosingService.IsCloud(VersionFolders.FolderName("379.48.2.30.74.150", cloud: true)));
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("Concluído 02:57", "", "Concluído 02:57")]
    [InlineData("", "Em andamento 40%", "Cloud: Em andamento 40%")]
    [InlineData("Concluído 02:57", "Em andamento 40%", "Local: Concluído 02:57 · Cloud: Em andamento 40%")]
    public void Column_text_shows_one_side_as_is_and_labels_both_when_there_are_two(string local, string cloud, string expected) =>
        Assert.Equal(expected, VersionFolders.ComposeColumn(local, cloud));

    [Theory]
    [InlineData(false, 0, 0, false, FolderState.Missing)]
    [InlineData(true, 3, 0, false, FolderState.Open)]    // gerada, nada compactado
    [InlineData(true, 2, 5, false, FolderState.Open)]    // fechamento incompleto: sobrou .exe/.dll
    [InlineData(true, 0, 0, false, FolderState.Open)]    // sem arquivos nem ZIPs: nao provou fechamento
    [InlineData(true, 0, 5, true, FolderState.Open)]     // fechamento rodando agora
    [InlineData(true, 0, 5, false, FolderState.Closed)]  // tudo em ZIP
    public void A_folder_is_closed_only_when_everything_is_zipped(bool exists, int pending, int zips, bool active, FolderState expected) =>
        Assert.Equal(expected, VersionFolders.StateOf(exists, pending, zips, active));

    [Fact]
    public void State_is_read_from_the_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vf-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(FolderState.Missing, VersionFolders.StateOf(dir, false));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Autcom.exe"), "x");
            Assert.Equal(FolderState.Open, VersionFolders.StateOf(dir, false));
            File.Delete(Path.Combine(dir, "Autcom.exe"));
            File.WriteAllText(Path.Combine(dir, "Autcom.zip"), "x");
            Assert.Equal(FolderState.Closed, VersionFolders.StateOf(dir, false));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
