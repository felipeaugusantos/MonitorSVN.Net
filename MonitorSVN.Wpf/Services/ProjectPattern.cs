using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorSVN.Services;

/// <summary>Arquivo (ou alternativas) que a versao precisa ter. Mesmo JSON do project_pattern.json do VarreduraSistema.</summary>
public sealed class PatternGroup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("accepted_files")] public List<string> AcceptedFiles { get; set; } = new();
    [JsonPropertyName("validate_version")] public bool ValidateVersion { get; set; } = true;

    public PatternGroup Clone() => new() { Name = Name, AcceptedFiles = AcceptedFiles.ToList(), ValidateVersion = ValidateVersion };
}

/// <summary>Padrao aprendido de um projeto (chave "379.48.2.30.74.150|local" ou "|cloud").</summary>
public sealed class ProjectPattern
{
    [JsonPropertyName("source_project")] public string SourceProject { get; set; } = "";
    [JsonPropertyName("source_path")] public string SourcePath { get; set; } = "";
    [JsonPropertyName("expected_file_version")] public string ExpectedFileVersion { get; set; } = "";
    [JsonPropertyName("expected_product_version")] public string ExpectedProductVersion { get; set; } = "";
    [JsonPropertyName("required_groups")] public List<PatternGroup> RequiredGroups { get; set; } = new();
}

public sealed class PatternData
{
    /// <summary>Padrao geral (vale quando o projeto ainda nao aprendeu o seu).</summary>
    [JsonPropertyName("required_groups")] public List<PatternGroup> RequiredGroups { get; set; } = new();
    [JsonPropertyName("project_patterns")] public Dictionary<string, ProjectPattern> ProjectPatterns { get; set; } = new();

    /// <summary>O que o sistema Python gravou e este nao usa (ex.: source_projects) e preservado ao regravar.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Regras de montagem dos grupos de arquivos obrigatorios (porta do pattern.py/config.py do Python).</summary>
public static class PatternRules
{
    public static readonly string[] SupportedExtensions = { ".exe", ".dll" };
    public static readonly string[] OptionalFileNames = { "libfuncoes.dll" };
    private static readonly string[] VersionGroupKeys = { "autcom", "autcomtinta", "autban", "auttin" };

    public static readonly IReadOnlyDictionary<string, string[]> KnownAlternatives = new Dictionary<string, string[]>
    {
        ["autban"] = new[] { "libAutban.dll", "libAutban.exe", "AutBan.exe" },
        ["auttin"] = new[] { "libAuttin.dll", "libAuttin.exe", "Auttin.exe" }
    };

    /// <summary>Grupos fixos de toda versao padrao.</summary>
    public static List<PatternGroup> CoreGroups() => new()
    {
        new() { Name = "Autcom", AcceptedFiles = { "Autcom.exe" } },
        new() { Name = "AutcomTinta", AcceptedFiles = { "AutcomTinta.exe", "AutcomTinta.dll" } },
        new() { Name = "Autban", AcceptedFiles = { "libAutban.dll", "libAutban.exe", "AutBan.exe" } },
        new() { Name = "Auttin", AcceptedFiles = { "libAuttin.dll", "libAuttin.exe", "Auttin.exe" } }
    };

    private static string BaseName(string folderName) =>
        folderName.EndsWith("_CLOUD", StringComparison.OrdinalIgnoreCase) ? folderName[..^6] : folderName;

    /// <summary>379.48.2.30.74.150.191 -> 379.48.2.30.74.150 (as 6 primeiras partes).</summary>
    public static string PatternKey(string folderName)
    {
        var parts = BaseName(folderName).Split('.');
        return parts.Length >= 6 ? string.Join('.', parts[..6]) : BaseName(folderName);
    }

    public static string Scope(string folderName) =>
        $"{PatternKey(folderName)}|{(ClosingService.IsCloud(folderName) ? "cloud" : "local")}";

    public static bool IsSupportedFile(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (name.StartsWith("err_", StringComparison.OrdinalIgnoreCase)) return false;
        if (OptionalFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return SupportedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>"libAutban.dll" -> "autban": o nome sem extensao, minusculo, sem o prefixo lib.</summary>
    public static string GroupKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return stem.StartsWith("lib", StringComparison.Ordinal) ? stem[3..] : stem;
    }

    private static string GroupNameFromStem(string stem) => stem.ToLowerInvariant() switch
    {
        "autcom" => "Autcom",
        "autcomtinta" => "AutcomTinta",
        "autban" or "libautban" => "Autban",
        "auttin" or "libauttin" => "Auttin",
        _ => stem
    };

    /// <summary>Inclui o arquivo no grupo dele (criando-o). Devolve o grupo, ou null se o arquivo nao conta.</summary>
    public static PatternGroup? AddRequiredFile(Dictionary<string, PatternGroup> groups, string fileName)
    {
        if (!IsSupportedFile(fileName)) return null;
        var key = GroupKey(fileName);
        if (!groups.TryGetValue(key, out var group))
        {
            group = new PatternGroup
            {
                Name = GroupNameFromStem(Path.GetFileNameWithoutExtension(fileName)),
                ValidateVersion = VersionGroupKeys.Contains(key)
            };
            groups[key] = group;
        }
        var name = Path.GetFileName(fileName);
        if (!group.AcceptedFiles.Contains(name, StringComparer.Ordinal)) group.AcceptedFiles.Add(name);
        return group;
    }

    /// <summary>Acrescenta as alternativas conhecidas (AutBan.exe, libAutban.dll...) e tira os arquivos opcionais.</summary>
    public static List<string> ExpandKnownAlternatives(string groupName, IEnumerable<string> acceptedFiles)
    {
        var accepted = acceptedFiles.ToList();
        var files = new HashSet<string>(accepted, StringComparer.Ordinal);
        var normalized = groupName.ToLowerInvariant();
        foreach (var file in accepted)
        {
            var stem = GroupKey(file);
            normalized = stem;
            if (KnownAlternatives.TryGetValue(stem, out var alternatives)) files.UnionWith(alternatives);
        }
        if (KnownAlternatives.TryGetValue(normalized, out var more)) files.UnionWith(more);

        return files.Where(f => !OptionalFileNames.Contains(f, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>Grupos prontos para uso (alternativas conhecidas incluidas). Null se nao houver nenhum.</summary>
    public static List<PatternGroup>? Normalize(IEnumerable<PatternGroup>? groups)
    {
        if (groups is null) return null;
        var result = new List<PatternGroup>();
        foreach (var group in groups)
        {
            var accepted = ExpandKnownAlternatives(group.Name, group.AcceptedFiles);
            if (accepted.Count == 0) continue;
            result.Add(new PatternGroup
            {
                Name = string.IsNullOrEmpty(group.Name) ? Path.GetFileNameWithoutExtension(accepted[0]) : group.Name,
                AcceptedFiles = accepted,
                ValidateVersion = group.ValidateVersion
            });
        }
        return result.Count > 0 ? result : null;
    }

    private static HashSet<string> Stems(PatternGroup group) =>
        group.AcceptedFiles.Select(GroupKey).ToHashSet();

    /// <summary>Garante os 4 grupos fixos: so entram os que o padrao ainda nao cobre.</summary>
    public static List<PatternGroup> MergeCoreGroups(IEnumerable<PatternGroup> groups)
    {
        var merged = groups.ToList();
        var existing = new HashSet<string>();
        foreach (var g in merged) existing.UnionWith(Stems(g));
        foreach (var core in CoreGroups())
        {
            if (Stems(core).Overlaps(existing)) continue;
            merged.Add(core);
            existing.UnionWith(Stems(core));
        }
        return merged;
    }

    public static List<PatternGroup> EnsureAutcom(IEnumerable<PatternGroup>? groups)
    {
        var list = groups?.ToList() ?? new List<PatternGroup>();
        if (!list.Any(g => Stems(g).Contains("autcom"))) list.Insert(0, CoreGroups()[0]);
        return list;
    }
}

/// <summary>
/// Guarda o padrao de arquivos de cada versao (project_pattern.json), aprendido a cada fechamento.
/// Com mais de um operador, fica na pasta de controle compartilhada para todos aprenderem e usarem o mesmo.
/// O formato e o do VarreduraSistema, entao o arquivo dele pode ser copiado para ca (ou o contrario).
/// </summary>
public sealed class ProjectPatternStore
{
    public const string FileName = "project_pattern.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly object _gate = new();
    private readonly string _path;

    public ProjectPatternStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, FileName);
    }

    public string FilePath => _path;

    /// <summary>Pasta de dados local (a mesma do settings.json), usada quando nao ha pasta de controle.</summary>
    public static string LocalFolder() =>
        Environment.GetEnvironmentVariable("MONITORSVN_DATA")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSVN");

    /// <summary>Le o arquivo. Se estiver corrompido, guarda uma copia ao lado e devolve null (o aprendizado recomeca).</summary>
    public PatternData? Read()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return null;
                return JsonSerializer.Deserialize<PatternData>(File.ReadAllText(_path, Encoding.UTF8));
            }
            catch (JsonException ex)
            {
                AppLog.Warn($"{_path} corrompido; copia guardada ao lado.", ex);
                try { File.Copy(_path, _path + ".corrompido.bak", overwrite: true); } catch (Exception io) when (io is IOException or UnauthorizedAccessException) { }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Nao foi possivel ler {_path}.", ex);
                return null;
            }
        }
    }

    /// <summary>Copia o padrao existente (ex.: o do VarreduraSistema) quando ainda nao ha arquivo aqui. Devolve true se importou.</summary>
    public bool SeedFrom(string existingFile)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path) || !File.Exists(existingFile)) return false;
                File.Copy(existingFile, _path);
                AppLog.Info($"Padrao de arquivos importado de {existingFile}.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Nao foi possivel importar o padrao de {existingFile}.", ex);
                return false;
            }
        }
    }

    /// <summary>Grupos exigidos deste projeto: o padrao aprendido para ele ou, sem isso, o padrao geral (mais os fixos).</summary>
    public List<PatternGroup> RequiredGroupsFor(string folderName)
    {
        var data = Read();
        if (ProjectGroups(data, folderName) is { } learned)
            return ClosingSpecific(folderName) ? PatternRules.EnsureAutcom(learned) : PatternRules.MergeCoreGroups(learned);
        if (ClosingSpecific(folderName)) return PatternRules.EnsureAutcom(null);
        return PatternRules.MergeCoreGroups(PatternRules.Normalize(data?.RequiredGroups) ?? PatternRules.CoreGroups());
    }

    private static bool ClosingSpecific(string folderName) => CopyRules.IsSpecificCopy(folderName);

    private static List<PatternGroup>? ProjectGroups(PatternData? data, string folderName) =>
        data is not null && data.ProjectPatterns.TryGetValue(PatternRules.Scope(folderName), out var p)
            ? PatternRules.Normalize(p.RequiredGroups)
            : null;

    /// <summary>true se este projeto ja tem padrao proprio aprendido.</summary>
    public bool HasLearned(string folderName) => ProjectGroups(Read(), folderName) is not null;

    /// <summary>Grava o padrao aprendido deste projeto, preservando o resto do arquivo.</summary>
    public string Save(string folderName, ProjectPattern pattern)
    {
        // outro operador pode gravar ao mesmo tempo (arquivo na rede): trava curta entre processos
        using var guard = AcquireWriteLock();
        lock (_gate)
        {
            var data = Read() ?? new PatternData();
            var key = PatternRules.Scope(folderName);
            data.ProjectPatterns[key] = pattern;

            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json), new UTF8Encoding(false));
                File.Move(tmp, _path, overwrite: true);
            }
            finally { try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            return key;
        }
    }

    private ProjectLock? AcquireWriteLock()
    {
        var folder = System.IO.Path.GetDirectoryName(_path)!;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { return new ProjectLock(folder, "Gravar padrao de arquivos", fileName: "project_pattern.lock").Acquire(); }
            catch (ProjectLockedException) { Thread.Sleep(250); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
        }
        return null;   // sem a trava a gravacao segue (e atomica); o pior caso e um aprendizado simultaneo sobrescrever o outro
    }
}
