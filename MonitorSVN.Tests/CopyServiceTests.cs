using System.IO.Compression;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class CopyRulesTests
{
    [Fact]
    public void Expected_versions_follow_the_folder_name()
    {
        Assert.Equal(("48.02.30.74", "02.30.74.150"), CopyRules.ExpectedVersions("379.48.2.30.74.150"));
        Assert.Equal(("48.02.30.74", "02.30.74.150"), CopyRules.ExpectedVersions("379.48.2.30.74.150_CLOUD"));
        Assert.Equal(("379.48.02.40", "48.02.40.05"), CopyRules.ExpectedVersions("379.48.2.40.5"));
        Assert.Null(CopyRules.ExpectedVersions("pasta-qualquer"));
        Assert.Null(CopyRules.ExpectedVersions("379.48.2.30"));
    }

    [Fact]
    public void Specific_copy_has_five_numeric_parts()
    {
        Assert.True(CopyRules.IsSpecificCopy("379.48.2.40.5"));
        Assert.True(CopyRules.IsSpecificCopy("379.48.2.40.5_CLOUD"));
        Assert.False(CopyRules.IsSpecificCopy("379.48.2.30.74.150"));
        Assert.False(CopyRules.IsSpecificCopy("379.48.2.40.x"));
    }

    [Fact]
    public void Local_accepts_the_name_and_the_LOCAL_suffix_cloud_only_the_name()
    {
        Assert.Equal(new[] { "379.48.2.30.74.150", "379.48.2.30.74.150_LOCAL" }, CopyRules.DestinationNames("379.48.2.30.74.150", cloud: false));
        Assert.Equal(new[] { "379.48.2.30.74.150_CLOUD" }, CopyRules.DestinationNames("379.48.2.30.74.150_CLOUD", cloud: true));
    }

    [Fact]
    public void New_destination_uses_hyphen_before_the_last_product_part()
    {
        Assert.Equal(Path.Combine(@"X:\ind", "02.30.74-150", "379.48.2.30.74.150_LOCAL"),
            CopyRules.NewDestinationFor("379.48.2.30.74.150", cloud: false, @"X:\ind"));
        Assert.Equal(Path.Combine(@"X:\ind", "02.30.74-150", "379.48.2.30.74.150_CLOUD"),
            CopyRules.NewDestinationFor("379.48.2.30.74.150_CLOUD", cloud: true, @"X:\ind"));
        Assert.Null(CopyRules.NewDestinationFor("379.48.2.40.5", cloud: false, @"X:\ind"));
    }

    [Fact]
    public void Autcom_size_limits_leave_a_gap_between_100_and_150()
    {
        Assert.True(CopyRules.LocalCopyAllowed(99.9));
        Assert.False(CopyRules.LocalCopyAllowed(100));
        Assert.False(CopyRules.CloudCopyAllowed(149.9));
        Assert.True(CopyRules.CloudCopyAllowed(150));
        Assert.False(CopyRules.LocalCopyAllowed(null));
        Assert.False(CopyRules.CloudCopyAllowed(null));
    }

    [Fact]
    public void Reads_the_autcom_version_and_size_from_inside_the_zip()
    {
        using var dir = new TempFolder();
        using (var zip = ZipFile.Open(Path.Combine(dir.Path, "Autcom.zip"), ZipArchiveMode.Create))
            zip.CreateEntryFromFile(typeof(object).Assembly.Location, "Autcom.exe");

        var info = CopyRules.ReadAutcom(dir.Path);

        Assert.Equal("Zip", info.Source);
        Assert.NotNull(info.FileVersion);
        Assert.True(info.SizeMb > 1);
        Assert.Null(info.Error);
    }

    [Fact]
    public void Missing_autcom_is_reported_and_safety_checks_block()
    {
        using var dir = new TempFolder();
        var project = Path.Combine(dir.Path, "379.48.2.30.74.150");
        Directory.CreateDirectory(project);
        var root = Path.Combine(dir.Path, "destinos");
        var destination = Path.Combine(root, "379.48.2.30.74.150_LOCAL");

        var info = CopyRules.ReadAutcom(project);
        var checks = CopyRules.SafetyChecks(project, destination, new[] { root }, cloud: false);

        Assert.Equal("Ausente", info.Source);
        Assert.True(checks.Single(c => c.Label == "Nome do destino").Ok);
        Assert.True(checks.Single(c => c.Label == "Destino dentro da raiz configurada").Ok);
        Assert.False(checks.Single(c => c.Label == "FileVersion").Ok);
        Assert.False(checks.Single(c => c.Label == "Limite Local").Ok);
    }

    [Fact]
    public void Safety_checks_block_a_destination_with_the_wrong_name_or_outside_the_roots()
    {
        using var dir = new TempFolder();
        var project = Path.Combine(dir.Path, "379.48.2.30.74.150");
        Directory.CreateDirectory(project);

        var checks = CopyRules.SafetyChecks(project, Path.Combine(dir.Path, "fora", "outra-pasta"),
            new[] { Path.Combine(dir.Path, "destinos") }, cloud: false);

        Assert.False(checks.Single(c => c.Label == "Nome do destino").Ok);
        Assert.False(checks.Single(c => c.Label == "Destino dentro da raiz configurada").Ok);
    }
}

public class DestinationFinderTests
{
    [Fact]
    public void Finds_by_name_case_insensitively_and_does_not_descend_into_a_match()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(Path.Combine(dir.Path, "02.30.74-150", "V1"));
        Directory.CreateDirectory(Path.Combine(dir.Path, "outra", "v1", "v1"));   // dentro de um destino: ignorado

        var found = DestinationFinder.Find(new[] { dir.Path }, new[] { "V1" }, null, CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.Contains(Path.Combine(dir.Path, "02.30.74-150", "V1"), found);
        Assert.Contains(Path.Combine(dir.Path, "outra", "v1"), found);
    }

    [Fact]
    public void Depth_limit_stops_the_search()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(Path.Combine(dir.Path, "a", "b", "c", "alvo"));

        Assert.Empty(DestinationFinder.Find(new[] { dir.Path }, new[] { "alvo" }, null, CancellationToken.None, maxDepth: 3));
        Assert.Single(DestinationFinder.Find(new[] { dir.Path }, new[] { "alvo" }, null, CancellationToken.None, maxDepth: 4));
    }

    [Fact]
    public void Cancellation_aborts_the_search_and_missing_roots_are_skipped()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(Path.Combine(dir.Path, "a"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DestinationFinder.Find(new[] { dir.Path }, new[] { "x" }, null, cts.Token));
        Assert.Empty(DestinationFinder.Find(new[] { Path.Combine(dir.Path, "nao-existe") }, new[] { "x" }, null, CancellationToken.None));
    }
}

public class CopyServiceTests
{
    private sealed class Inline : IProgress<CopyProgress>
    {
        private readonly Action<CopyProgress> _action;
        public Inline(Action<CopyProgress> action) => _action = action;
        public void Report(CopyProgress value) => _action(value);
    }

    private static void MakeZip(string folder, string zipName, string content = "conteudo")
    {
        var exeName = Path.GetFileNameWithoutExtension(zipName) + ".exe";
        using var zip = ZipFile.Open(Path.Combine(folder, zipName), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(exeName).Open());
        writer.Write(content);
    }

    private static (string Source, string Destination) Setup(TempFolder dir)
    {
        var source = Path.Combine(dir.Path, "origem");
        var destination = Path.Combine(dir.Path, "destino");
        Directory.CreateDirectory(Path.Combine(source, "comandosCMD"));
        Directory.CreateDirectory(destination);
        return (source, destination);
    }

    [Fact]
    public async Task Copies_verifies_and_removes_only_the_copied_zips_from_the_source()
    {
        using var dir = new TempFolder();
        var (source, destination) = Setup(dir);
        MakeZip(source, "autcom.zip");
        MakeZip(source, "libfuncoes.zip");
        File.WriteAllText(Path.Combine(source, "leia-me.txt"), "nao e zip");
        File.WriteAllText(Path.Combine(source, ProjectLock.FileName), "{}");
        MakeZip(destination, "antigo.zip");

        var result = await CopyService.CopyAsync(source, destination, null, CancellationToken.None);

        Assert.Equal(2, result.CopiedFiles);
        Assert.Equal(2, result.CleanedItems);
        Assert.Null(result.CleanupError);
        Assert.Equal(new[] { "leia-me.txt" }, result.RemainingItems);
        Assert.Equal(new[] { "antigo.zip" }, result.OnlyInDestination);
        Assert.True(File.Exists(Path.Combine(destination, "autcom.zip")));
        Assert.True(File.Exists(Path.Combine(destination, "libfuncoes.zip")));
        Assert.False(File.Exists(Path.Combine(source, "autcom.zip")));
        Assert.True(Directory.Exists(Path.Combine(source, "comandosCMD")));
        Assert.True(File.Exists(Path.Combine(source, ProjectLock.FileName)));
        Assert.Equal(2, result.Hashes.Count);
        Assert.Equal(64, result.Hashes["autcom.zip"].Length);
        Assert.Empty(Directory.GetDirectories(destination, CopyService.TransactionPrefix + "*"));   // pasta temporaria removida
    }

    [Fact]
    public async Task Replaces_an_existing_file_and_keeps_the_new_content()
    {
        using var dir = new TempFolder();
        var (source, destination) = Setup(dir);
        MakeZip(source, "autcom.zip", "versao nova");
        MakeZip(destination, "autcom.zip", "versao velha");
        var expected = File.ReadAllBytes(Path.Combine(source, "autcom.zip"));

        await CopyService.CopyAsync(source, destination, null, CancellationToken.None);

        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(destination, "autcom.zip")));
    }

    [Fact]
    public async Task Failure_in_the_middle_rolls_back_and_leaves_the_source_untouched()
    {
        using var dir = new TempFolder();
        var (source, destination) = Setup(dir);
        MakeZip(source, "a.zip", "a nova");
        MakeZip(source, "b.zip", "b nova");
        MakeZip(destination, "a.zip", "a velha");
        var oldA = File.ReadAllBytes(Path.Combine(destination, "a.zip"));
        Directory.CreateDirectory(Path.Combine(destination, "b.zip"));   // impede a promocao do segundo arquivo

        await Assert.ThrowsAnyAsync<IOException>(() => CopyService.CopyAsync(source, destination, null, CancellationToken.None));

        Assert.Equal(oldA, File.ReadAllBytes(Path.Combine(destination, "a.zip")));   // a velha foi restaurada
        Assert.True(File.Exists(Path.Combine(source, "a.zip")));
        Assert.True(File.Exists(Path.Combine(source, "b.zip")));
        Assert.Empty(Directory.GetDirectories(destination, CopyService.TransactionPrefix + "*"));
    }

    [Fact]
    public async Task Cancelling_before_the_end_changes_nothing()
    {
        using var dir = new TempFolder();
        var (source, destination) = Setup(dir);
        MakeZip(source, "autcom.zip");
        using var cts = new CancellationTokenSource();
        var progress = new Inline(p => { if (p.Phase == "Copiando") cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CopyService.CopyAsync(source, destination, progress, cts.Token));

        Assert.False(File.Exists(Path.Combine(destination, "autcom.zip")));
        Assert.True(File.Exists(Path.Combine(source, "autcom.zip")));
        Assert.Empty(Directory.GetDirectories(destination, CopyService.TransactionPrefix + "*"));
    }

    [Fact]
    public async Task Source_changed_during_the_copy_is_detected()
    {
        using var dir = new TempFolder();
        var (source, destination) = Setup(dir);
        MakeZip(source, "a.zip");
        MakeZip(source, "b.zip");
        var changed = false;
        var progress = new Inline(p =>
        {
            if (p.Phase != "Verificando" || changed) return;
            changed = true;
            File.AppendAllText(Path.Combine(source, "b.zip"), "x");
        });

        var ex = await Assert.ThrowsAnyAsync<IOException>(() => CopyService.CopyAsync(source, destination, progress, CancellationToken.None));

        Assert.Contains("b.zip", ex.Message);
        Assert.False(File.Exists(Path.Combine(destination, "a.zip")));
        Assert.True(File.Exists(Path.Combine(source, "a.zip")));
    }

    [Fact]
    public async Task Zip_with_a_wrong_name_or_no_zips_blocks_the_copy()
    {
        using var dir = new TempFolder();
        var (source, destination) = Setup(dir);
        await Assert.ThrowsAnyAsync<IOException>(() => CopyService.CopyAsync(source, destination, null, CancellationToken.None));

        using (var zip = ZipFile.Open(Path.Combine(source, "errado.zip"), ZipArchiveMode.Create))
            zip.CreateEntry("autcom.exe");
        var ex = await Assert.ThrowsAnyAsync<IOException>(() => CopyService.CopyAsync(source, destination, null, CancellationToken.None));
        Assert.Contains("ZIP invalido", ex.Message);
    }

    [Fact]
    public void Old_temporary_folders_are_cleaned_but_incomplete_rollbacks_are_kept()
    {
        using var dir = new TempFolder();
        var old = Path.Combine(dir.Path, CopyService.TransactionPrefix + "velha");
        var keep = Path.Combine(dir.Path, CopyService.TransactionPrefix + "backup");
        var recent = Path.Combine(dir.Path, CopyService.TransactionPrefix + "recente");
        foreach (var d in new[] { old, keep, recent }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(keep, "ROLLBACK_INCOMPLETO.txt"), "x");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        Directory.SetLastWriteTimeUtc(keep, DateTime.UtcNow.AddDays(-3));

        var removed = CopyService.CleanOrphans(dir.Path);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(keep));
        Assert.True(Directory.Exists(recent));
    }
}
