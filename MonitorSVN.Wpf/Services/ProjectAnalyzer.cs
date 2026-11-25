using System.Collections.Concurrent;
using System.IO.Compression;

namespace MonitorSVN.Services;

/// <summary>Resultado da conferencia de um grupo de arquivos (na pasta ou dentro do ZIP).</summary>
public sealed class FileCheck
{
    public required string GroupName { get; init; }
    public required IReadOnlyList<string> AcceptedFiles { get; init; }
    public bool ValidateVersion { get; init; }
    public string? FoundName { get; init; }
    /// <summary>"Pasta", "Zip" ou "Ausente".</summary>
    public string Source { get; init; } = "Ausente";
    public string? FileVersion { get; set; }
    public string? ProductVersion { get; set; }
    public double? SizeMb { get; init; }
    /// <summary>"OK", "Ausente" ou o erro de leitura do ZIP.</summary>
    public string Status { get; init; } = "Ausente";
}

public sealed class ProjectAnalysis
{
    /// <summary>"OK" ou os problemas separados por ponto e virgula.</summary>
    public required string Status { get; init; }
    public bool Ok => Status == "OK";
    public required string ExpectedFileVersion { get; init; }
    public required string ExpectedProductVersion { get; init; }
    public required List<FileCheck> FileChecks { get; init; }
    public List<string> ZipNameErrors { get; init; } = new();
}

/// <summary>
/// Confere a pasta de uma versao contra o padrao de arquivos (o "Status OK" do VarreduraSistema) e aprende o padrao
/// de cada versao a cada fechamento. Leitura de versao injetavel para os testes.
/// </summary>
public sealed class ProjectAnalyzer
{
    private readonly ProjectPatternStore _patterns;
    private readonly Func<string, VersionReader.Versions?> _readVersions;
    private static readonly ConcurrentDictionary<string, (long Ticks, long Length, ZipRead Result)> ZipCache = new();

    private sealed record ZipRead(string? FileVersion, string? ProductVersion, double? SizeMb, string? Error);

    public ProjectAnalyzer(ProjectPatternStore patterns, Func<string, VersionReader.Versions?>? readVersions = null)
    {
        _patterns = patterns;
        _readVersions = readVersions ?? VersionReader.Read;
    }

    // ---------------------------------------------------------------- conferencia

    public ProjectAnalysis Analyze(string projectPath)
    {
        projectPath = projectPath.TrimEnd('\\', '/');
        var name = Path.GetFileName(projectPath);
        var groups = _patterns.RequiredGroupsFor(name);
        var expected = CopyRules.ExpectedVersions(name);
        var zipIndex = ZipIndex(projectPath);
        var zipErrors = ZipNameValidator.Validate(projectPath);
        var checks = groups.Select(g => CheckGroup(projectPath, g, zipIndex)).ToList();

        return new ProjectAnalysis
        {
            Status = BuildStatus(expected?.File, expected?.Product, checks, zipErrors),
            ExpectedFileVersion = expected?.File ?? "",
            ExpectedProductVersion = expected?.Product ?? "",
            FileChecks = checks,
            ZipNameErrors = zipErrors
        };
    }

    private static Dictionary<string, string> ZipIndex(string projectPath) =>
        Directory.EnumerateFiles(projectPath, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last());

    private FileCheck CheckGroup(string projectPath, PatternGroup group, Dictionary<string, string> zipIndex)
    {
        var accepted = group.AcceptedFiles;
        FileCheck Make(string? found, string source, string? fv, string? pv, double? size, string status) => new()
        {
            GroupName = group.Name, AcceptedFiles = accepted, ValidateVersion = group.ValidateVersion,
            FoundName = found, Source = source, FileVersion = fv, ProductVersion = pv, SizeMb = size, Status = status
        };

        foreach (var file in accepted)
        {
            var path = Path.Combine(projectPath, file);
            if (!File.Exists(path)) continue;
            try
            {
                var size = new FileInfo(path).Length / 1024d / 1024d;
                var v = group.ValidateVersion ? _readVersions(path) : null;
                return Make(file, "Pasta", v?.File, v?.Product, size, "OK");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        FileCheck? lastZipError = null;
        foreach (var file in accepted)
        {
            if (!zipIndex.TryGetValue(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), out var zipPath)) continue;
            var read = ReadFromZip(zipPath, file, group.ValidateVersion);
            if (read.Error is not null)
            {
                lastZipError = Make(file, "Zip", read.FileVersion, read.ProductVersion, read.SizeMb, read.Error);
                continue;
            }
            return Make(file, "Zip", read.FileVersion, read.ProductVersion, read.SizeMb, "OK");
        }
        return lastZipError ?? Make(null, "Ausente", null, null, null, "Ausente");
    }

    /// <summary>Tamanho e (opcionalmente) versao de um arquivo dentro do ZIP; o resultado fica em cache enquanto o ZIP nao muda.</summary>
    private ZipRead ReadFromZip(string zipPath, string fileName, bool readVersion)
    {
        FileInfo info;
        try { info = new FileInfo(zipPath); if (!info.Exists) return new ZipRead(null, null, null, "Zip ausente"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new ZipRead(null, null, null, $"Erro ao ler zip: {ex.Message}"); }

        var key = $"{zipPath.ToLowerInvariant()}|{fileName.ToLowerInvariant()}|{readVersion}";
        if (ZipCache.TryGetValue(key, out var cached) && cached.Ticks == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length)
            return cached.Result;

        ZipRead result;
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) result = new ZipRead(null, null, null, $"{fileName} ausente no zip");
            else
            {
                var size = entry.Length / 1024d / 1024d;
                if (!readVersion) result = new ZipRead(null, null, size, null);
                else
                {
                    var temp = Path.Combine(Path.GetTempPath(), "monitorsvn-pat-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temp);
                    try
                    {
                        var extracted = Path.Combine(temp, Path.GetFileName(entry.Name));
                        entry.ExtractToFile(extracted);
                        var v = _readVersions(extracted);
                        result = new ZipRead(v?.File, v?.Product, size, null);
                    }
                    finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
                }
            }
        }
        catch (InvalidDataException) { result = new ZipRead(null, null, null, "Zip invalido"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new ZipRead(null, null, null, $"Erro ao ler zip: {ex.Message}"); }

        ZipCache[key] = (info.LastWriteTimeUtc.Ticks, info.Length, result);
        return result;
    }

    private static string BuildStatus(string? expectedFile, string? expectedProduct, List<FileCheck> checks, List<string> zipNameErrors)
    {
        var errors = new List<string>();
        if (expectedFile is null || expectedProduct is null) errors.Add("Nome da pasta fora do padrao");

        var missing = checks.Where(c => c.Status == "Ausente").Select(c => string.Join(" ou ", c.AcceptedFiles)).ToList();
        if (missing.Count > 0) errors.Add($"Ausente: {string.Join(", ", missing)}");

        errors.AddRange(checks.Where(c => c.Source == "Zip" && c.Status != "OK").Select(c => $"{c.GroupName}: {c.Status}"));
        if (zipNameErrors.Count > 0)
        {
            var preview = string.Join("; ", zipNameErrors.Take(3));
            if (zipNameErrors.Count > 3) preview += $"; +{zipNameErrors.Count - 3} ZIP(s)";
            errors.Add($"Nome de ZIP incorreto: {preview}");
        }

        foreach (var c in checks.Where(c => c.Status == "OK" && c.ValidateVersion))
        {
            if (expectedFile is not null && c.FileVersion != expectedFile)
                errors.Add($"{c.GroupName} FileVersion {(string.IsNullOrEmpty(c.FileVersion) ? "ausente" : "incorreto")}");
            if (expectedProduct is not null && c.ProductVersion != expectedProduct)
                errors.Add($"{c.GroupName} ProductVersion {(string.IsNullOrEmpty(c.ProductVersion) ? "ausente" : "incorreto")}");
        }
        return errors.Count == 0 ? "OK" : string.Join("; ", errors);
    }

    // ---------------------------------------------------------------- aprendizado

    /// <summary>
    /// Aprende o padrao desta versao a partir do que ela tem agora (chamado antes do fechamento, com os .exe/.dll ainda
    /// na pasta). So entram arquivos presentes; os que validam versao so se a versao bater com a esperada.
    /// Copia especifica aprende todos os .exe/.dll que tem. Devolve a chave gravada, ou null se nao ha o que aprender.
    /// </summary>
    public string? Learn(string projectPath)
    {
        projectPath = projectPath.TrimEnd('\\', '/');
        var name = Path.GetFileName(projectPath);
        var expected = CopyRules.ExpectedVersions(name);
        if (expected is null) return null;
        var (expFile, expProduct) = expected.Value;

        var analysis = Analyze(projectPath);
        var checks = analysis.FileChecks.ToList();
        if (CopyRules.IsSpecificCopy(name)) checks.AddRange(DiscoverFiles(projectPath));

        var groups = new Dictionary<string, PatternGroup>();
        foreach (var check in checks)
        {
            if (check.FoundName is null || check.Status != "OK") continue;
            var matches = check.FileVersion == expFile && check.ProductVersion == expProduct;
            if (check.ValidateVersion && !matches) continue;
            var group = PatternRules.AddRequiredFile(groups, check.FoundName);
            if (group is not null && matches) group.ValidateVersion = true;
        }
        if (groups.Count == 0) return null;

        var learned = groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PatternGroup { Name = g.Name, AcceptedFiles = g.AcceptedFiles.Order(StringComparer.Ordinal).ToList(), ValidateVersion = g.ValidateVersion })
            .ToList();

        return _patterns.Save(name, new ProjectPattern
        {
            SourceProject = name,
            SourcePath = projectPath,
            ExpectedFileVersion = expFile,
            ExpectedProductVersion = expProduct,
            RequiredGroups = learned
        });
    }

    /// <summary>Todos os .exe/.dll da pasta e de dentro dos ZIPs, com a versao (usado para copias especificas).</summary>
    private List<FileCheck> DiscoverFiles(string projectPath)
    {
        var found = new List<FileCheck>();
        foreach (var path in Directory.EnumerateFiles(projectPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(path);
            if (!PatternRules.IsSupportedFile(file)) continue;
            var v = _readVersions(path);
            found.Add(new FileCheck { GroupName = Path.GetFileNameWithoutExtension(file), AcceptedFiles = new[] { file }, FoundName = file,
                Source = "Pasta", FileVersion = v?.File, ProductVersion = v?.Product, Status = "OK" });
        }

        var seen = new HashSet<string>(found.Select(f => f.FoundName!.ToLowerInvariant()));
        foreach (var zipPath in Directory.EnumerateFiles(projectPath, "*.zip").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            List<string> members;
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                members = archive.Entries.Where(e => e.Name.Length > 0 && PatternRules.IsSupportedFile(e.Name)).Select(e => e.Name).Distinct().ToList();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }

            foreach (var member in members)
            {
                if (!seen.Add(member.ToLowerInvariant())) continue;
                var read = ReadFromZip(zipPath, member, readVersion: true);
                if (read.Error is not null) continue;
                found.Add(new FileCheck { GroupName = Path.GetFileNameWithoutExtension(member), AcceptedFiles = new[] { member }, FoundName = member,
                    Source = "Zip", FileVersion = read.FileVersion, ProductVersion = read.ProductVersion, SizeMb = read.SizeMb, Status = "OK" });
            }
        }
        return found;
    }
}
