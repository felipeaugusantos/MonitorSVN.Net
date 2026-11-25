using System.IO.Compression;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class PatternRulesTests
{
    [Theory]
    [InlineData("379.48.2.30.74.150.191", "379.48.2.30.74.150|local")]
    [InlineData("379.48.2.30.74.150.191_CLOUD", "379.48.2.30.74.150|cloud")]
    [InlineData("379.48.2.30.74.150", "379.48.2.30.74.150|local")]
    [InlineData("379.48.2.40.5", "379.48.2.40.5|local")]
    public void Scope_uses_the_first_six_parts_and_local_or_cloud(string folder, string expected) =>
        Assert.Equal(expected, PatternRules.Scope(folder));

    [Theory]
    [InlineData("autcom.exe", true)]
    [InlineData("AutcomTinta.DLL", true)]
    [InlineData("err_autcom.exe", false)]
    [InlineData("libFuncoes.dll", false)]   // opcional
    [InlineData("pacote.bpl", false)]
    [InlineData("autcom.zip", false)]
    public void Supported_files(string name, bool expected) => Assert.Equal(expected, PatternRules.IsSupportedFile(name));

    [Fact]
    public void Known_alternatives_are_added_to_autban_and_auttin()
    {
        Assert.Equal(new[] { "AutBan.exe", "libAutban.dll", "libAutban.exe" },
            PatternRules.ExpandKnownAlternatives("Autban", new[] { "AutBan.exe" }));
        Assert.Equal(new[] { "Auttin.exe", "libAuttin.dll", "libAuttin.exe" },
            PatternRules.ExpandKnownAlternatives("Auttin", new[] { "libAuttin.dll" }));
        Assert.Equal(new[] { "Outro.dll" }, PatternRules.ExpandKnownAlternatives("Outro", new[] { "Outro.dll", "libFuncoes.dll" }));
    }

    [Fact]
    public void Add_required_file_groups_lib_prefixed_files_together_and_marks_version_groups()
    {
        var groups = new Dictionary<string, PatternGroup>();

        PatternRules.AddRequiredFile(groups, "libAutban.dll");
        PatternRules.AddRequiredFile(groups, "AutBan.exe");
        PatternRules.AddRequiredFile(groups, "Relatorio.dll");
        Assert.Null(PatternRules.AddRequiredFile(groups, "err_x.exe"));

        Assert.Equal(2, groups.Count);
        Assert.Equal("Autban", groups["autban"].Name);
        Assert.True(groups["autban"].ValidateVersion);
        Assert.Equal(new[] { "libAutban.dll", "AutBan.exe" }, groups["autban"].AcceptedFiles);
        Assert.False(groups["relatorio"].ValidateVersion);
    }

    [Fact]
    public void Core_groups_are_added_only_when_the_pattern_does_not_cover_them()
    {
        var learned = new List<PatternGroup>
        {
            new() { Name = "Autcom", AcceptedFiles = { "autcom.exe" } },
            new() { Name = "Autban", AcceptedFiles = { "AutBan.exe" } },
            new() { Name = "Outro", AcceptedFiles = { "Outro.dll" }, ValidateVersion = false }
        };

        var merged = PatternRules.MergeCoreGroups(learned);

        Assert.Equal(new[] { "Autcom", "Autban", "Outro", "AutcomTinta", "Auttin" }, merged.Select(g => g.Name));
    }

    [Fact]
    public void Ensure_autcom_inserts_it_first_when_missing()
    {
        var groups = PatternRules.EnsureAutcom(new List<PatternGroup> { new() { Name = "X", AcceptedFiles = { "x.exe" } } });
        Assert.Equal("Autcom", groups[0].Name);
        Assert.Single(PatternRules.EnsureAutcom(new List<PatternGroup> { new() { Name = "Autcom", AcceptedFiles = { "autcom.exe" } } }));
    }
}

public class ProjectPatternStoreTests
{
    // trecho no formato gravado pelo VarreduraSistema
    private const string PythonJson = """
    {
      "source_projects": [ { "name": "379.48.2.30.74.111.172", "path": "C:\\VERSOES_FECHADAS\\379.48.2.30.74.111.172" } ],
      "required_groups": [
        { "name": "Autcom", "accepted_files": [ "autcom.exe" ], "validate_version": true },
        { "name": "Geral", "accepted_files": [ "Geral.dll" ], "validate_version": false }
      ],
      "project_patterns": {
        "379.48.2.30.74.111|local": {
          "source_project": "379.48.2.30.74.111.172",
          "source_path": "C:\\VERSOES_FECHADAS\\379.48.2.30.74.111.172",
          "expected_file_version": "48.02.30.74",
          "expected_product_version": "02.30.74.111",
          "required_groups": [
            { "name": "Autcom", "accepted_files": [ "autcom.exe" ], "validate_version": true },
            { "name": "ConsultaPix", "accepted_files": [ "ConsultaPix.exe" ], "validate_version": false }
          ]
        }
      }
    }
    """;

    private static ProjectPatternStore StoreWith(TempFolder dir, string? json)
    {
        if (json is not null) File.WriteAllText(Path.Combine(dir.Path, ProjectPatternStore.FileName), json);
        return new ProjectPatternStore(dir.Path);
    }

    [Fact]
    public void Learned_pattern_wins_over_the_general_one_and_core_groups_are_merged()
    {
        using var dir = new TempFolder();
        var store = StoreWith(dir, PythonJson);

        var learned = store.RequiredGroupsFor("379.48.2.30.74.111.172").Select(g => g.Name).ToList();
        var general = store.RequiredGroupsFor("379.48.2.30.74.150.10").Select(g => g.Name).ToList();   // sem padrao proprio

        Assert.Equal(new[] { "Autcom", "ConsultaPix", "AutcomTinta", "Autban", "Auttin" }, learned);
        Assert.Equal(new[] { "Autcom", "Geral", "AutcomTinta", "Autban", "Auttin" }, general);
        Assert.True(store.HasLearned("379.48.2.30.74.111.172"));
        Assert.False(store.HasLearned("379.48.2.30.74.150.10"));
        Assert.False(store.HasLearned("379.48.2.30.74.111.172_CLOUD"));   // escopo cloud e separado
    }

    [Fact]
    public void Without_a_file_the_four_core_groups_are_required()
    {
        using var dir = new TempFolder();
        Assert.Equal(new[] { "Autcom", "AutcomTinta", "Autban", "Auttin" },
            StoreWith(dir, null).RequiredGroupsFor("379.48.2.30.74.150.1").Select(g => g.Name));
    }

    [Fact]
    public void Specific_copy_uses_only_what_it_learned_plus_autcom()
    {
        using var dir = new TempFolder();
        var store = StoreWith(dir, null);
        Assert.Equal(new[] { "Autcom" }, store.RequiredGroupsFor("379.48.2.40.5").Select(g => g.Name));
    }

    [Fact]
    public void Save_keeps_the_rest_of_the_file_and_the_python_fields()
    {
        using var dir = new TempFolder();
        var store = StoreWith(dir, PythonJson);

        var key = store.Save("379.48.2.30.74.150.191_CLOUD", new ProjectPattern
        {
            SourceProject = "379.48.2.30.74.150.191_CLOUD",
            RequiredGroups = { new PatternGroup { Name = "Autcom", AcceptedFiles = { "autcom.exe" } } }
        });

        Assert.Equal("379.48.2.30.74.150|cloud", key);
        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("source_projects", text);                    // campo so do Python preservado
        Assert.Contains("379.48.2.30.74.111|local", text);           // padrao anterior intacto
        Assert.Contains("379.48.2.30.74.150|cloud", text);
        Assert.True(new ProjectPatternStore(dir.Path).HasLearned("379.48.2.30.74.150.191_CLOUD"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Corrupted_file_is_kept_aside_and_learning_restarts()
    {
        using var dir = new TempFolder();
        var store = StoreWith(dir, "{ nao e json");

        Assert.Null(store.Read());
        Assert.True(File.Exists(store.FilePath + ".corrompido.bak"));
        store.Save("379.48.2.30.74.150.1", new ProjectPattern { RequiredGroups = { new PatternGroup { Name = "Autcom", AcceptedFiles = { "autcom.exe" } } } });
        Assert.True(store.HasLearned("379.48.2.30.74.150.1"));
    }

    [Fact]
    public void Seed_copies_an_existing_file_only_when_there_is_none()
    {
        using var dir = new TempFolder();
        using var other = new TempFolder();
        var seed = Path.Combine(other.Path, "project_pattern.json");
        File.WriteAllText(seed, PythonJson);
        var store = new ProjectPatternStore(dir.Path);

        Assert.True(store.SeedFrom(seed));
        Assert.True(store.HasLearned("379.48.2.30.74.111.172"));

        File.WriteAllText(seed, "{}");
        Assert.False(store.SeedFrom(seed));   // ja existe: nao sobrescreve o que foi aprendido aqui
        Assert.True(store.HasLearned("379.48.2.30.74.111.172"));
    }
}

public class ProjectAnalyzerTests
{
    private const string Name = "379.48.2.30.74.150.191";           // esperado: 48.02.30.74 / 02.30.74.150
    private const string Specific = "379.48.2.40.5";                 // esperado: 379.48.02.40 / 48.02.40.05
    private static readonly VersionReader.Versions Good = new("48.02.30.74", "02.30.74.150");

    /// <summary>Le a versao de mentira escrita no proprio arquivo ("arquivo|produto"), para nao depender de executaveis reais.</summary>
    private static VersionReader.Versions? Fake(string path)
    {
        var text = File.ReadAllText(path);
        var parts = text.Split('|');
        return parts.Length == 2 ? new VersionReader.Versions(parts[0], parts[1]) : null;
    }

    private static void Put(string folder, string file, VersionReader.Versions? v = null)
    {
        var version = v ?? Good;
        File.WriteAllText(Path.Combine(folder, file), $"{version.File}|{version.Product}");
    }

    private static string Project(TempFolder dir, string name = Name, bool withCore = true)
    {
        var project = Path.Combine(dir.Path, name);
        Directory.CreateDirectory(project);
        if (withCore)
            foreach (var f in new[] { "autcom.exe", "AutcomTinta.dll", "libAutban.dll", "libAuttin.dll" }) Put(project, f);
        return project;
    }

    private static (ProjectAnalyzer Analyzer, ProjectPatternStore Store) Create(TempFolder dir)
    {
        var store = new ProjectPatternStore(Path.Combine(dir.Path, "dados"));
        return (new ProjectAnalyzer(store, Fake), store);
    }

    [Fact]
    public void Complete_folder_with_right_versions_is_ok()
    {
        using var dir = new TempFolder();
        var (analyzer, _) = Create(dir);

        var analysis = analyzer.Analyze(Project(dir));

        Assert.True(analysis.Ok);
        Assert.Equal("48.02.30.74", analysis.ExpectedFileVersion);
        Assert.All(analysis.FileChecks, c => Assert.Equal("Pasta", c.Source));
    }

    [Fact]
    public void Missing_file_wrong_version_and_bad_folder_name_are_reported()
    {
        using var dir = new TempFolder();
        var (analyzer, _) = Create(dir);
        var project = Project(dir);
        File.Delete(Path.Combine(project, "libAuttin.dll"));
        Put(project, "autcom.exe", new VersionReader.Versions("1.0.0.0", "02.30.74.150"));

        var status = analyzer.Analyze(project).Status;

        Assert.Contains("Ausente: libAuttin.dll ou libAuttin.exe ou Auttin.exe", status);
        Assert.Contains("Autcom FileVersion incorreto", status);
        Assert.DoesNotContain("Autcom ProductVersion", status);
        Assert.Contains("Nome da pasta fora do padrao", analyzer.Analyze(Project(dir, "pasta-qualquer")).Status);
    }

    [Fact]
    public void Files_zipped_by_the_closing_are_checked_inside_the_zip()
    {
        using var dir = new TempFolder();
        var (analyzer, _) = Create(dir);
        var project = Project(dir, withCore: false);
        foreach (var f in new[] { "autcom.exe", "AutcomTinta.dll", "libAutban.dll", "libAuttin.dll" })
        {
            using var zip = ZipFile.Open(Path.Combine(project, Path.GetFileNameWithoutExtension(f) + ".zip"), ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry(f).Open());
            writer.Write($"{Good.File}|{Good.Product}");
        }

        var analysis = analyzer.Analyze(project);

        Assert.True(analysis.Ok, analysis.Status);
        Assert.All(analysis.FileChecks, c => Assert.Equal("Zip", c.Source));
    }

    [Fact]
    public void Learn_records_the_files_with_the_expected_version_and_the_next_analysis_uses_them()
    {
        using var dir = new TempFolder();
        var (analyzer, store) = Create(dir);
        var project = Project(dir);

        var key = analyzer.Learn(project);

        Assert.Equal("379.48.2.30.74.150|local", key);
        Assert.True(store.HasLearned(Name));
        var groups = store.RequiredGroupsFor(Name);
        Assert.True(groups.Single(g => g.Name == "Autcom").ValidateVersion);
        Assert.Contains("AutBan.exe", groups.Single(g => g.Name == "Autban").AcceptedFiles);   // alternativas conhecidas
    }

    [Fact]
    public void Learn_skips_version_checked_files_whose_version_does_not_match_and_unknown_names()
    {
        using var dir = new TempFolder();
        var (analyzer, store) = Create(dir);
        var project = Project(dir);
        Put(project, "AutcomTinta.dll", new VersionReader.Versions("9.9.9.9", "9.9.9.9"));

        analyzer.Learn(project);

        Assert.DoesNotContain(store.Read()!.ProjectPatterns["379.48.2.30.74.150|local"].RequiredGroups, g => g.Name == "AutcomTinta");
        Assert.Null(analyzer.Learn(Project(dir, "pasta-qualquer")));
    }

    [Fact]
    public void Specific_copy_learns_every_exe_and_dll_it_has_including_the_ones_inside_zips()
    {
        using var dir = new TempFolder();
        var (analyzer, store) = Create(dir);
        var project = Project(dir, Specific, withCore: false);
        var v = new VersionReader.Versions("379.48.02.40", "48.02.40.05");
        Put(project, "autcom.exe", v);
        Put(project, "Outro.dll", v);
        using (var zip = ZipFile.Open(Path.Combine(project, "NoZip.zip"), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("NoZip.dll").Open()))
            writer.Write($"{v.File}|{v.Product}");

        var key = analyzer.Learn(project);

        Assert.Equal("379.48.2.40.5|local", key);
        var names = store.RequiredGroupsFor(Specific).Select(g => g.Name).ToList();
        Assert.Contains("Autcom", names);
        Assert.Contains("Outro", names);
        Assert.Contains("NoZip", names);
        Assert.True(store.RequiredGroupsFor(Specific).Single(g => g.Name == "Outro").ValidateVersion);   // versao bateu: passa a validar
    }

    [Fact]
    public void Safety_checks_block_the_copy_when_the_project_status_is_not_ok()
    {
        using var dir = new TempFolder();
        var (analyzer, _) = Create(dir);
        var project = Project(dir);
        var root = Path.Combine(dir.Path, "destinos");
        var destination = Path.Combine(root, Name + "_LOCAL");

        var ok = CopyRules.SafetyChecks(project, destination, new[] { root }, cloud: false, analyzer);
        File.Delete(Path.Combine(project, "AutcomTinta.dll"));
        var broken = CopyRules.SafetyChecks(project, destination, new[] { root }, cloud: false, analyzer);

        Assert.True(ok.Single(c => c.Label == "Status do projeto").Ok);
        Assert.False(broken.Single(c => c.Label == "Status do projeto").Ok);
    }

    [Fact]
    public async Task Closing_learns_the_pattern_before_running_the_bat()
    {
        using var dir = new TempFolder();
        var (analyzer, store) = Create(dir);
        var project = Project(dir);
        var commands = Path.Combine(project, "comandosCMD");
        Directory.CreateDirectory(commands);
        File.WriteAllText(Path.Combine(commands, ClosingService.BatFileName),
            "@echo off\r\ndel /q \"%~dp0..\\*.exe\" \"%~dp0..\\*.dll\"\r\nexit /b 1\r\n");
        var service = new ClosingService(dir.Path, analyzer: analyzer);

        Assert.Null(service.Enqueue(project));
        for (var i = 0; i < 400 && service.IsActive(project); i++) await Task.Delay(50);

        Assert.Equal(ClosingState.Done, service.JobFor(project)!.State);
        Assert.True(store.HasLearned(Name));   // aprendeu antes do BAT apagar os arquivos
    }
}
