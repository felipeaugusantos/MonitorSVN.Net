using System.IO.Compression;

namespace MonitorSVN.Services;

/// <summary>Versao do Autcom lida do disco (pasta ou ZIP) e o tamanho do executavel.</summary>
public sealed record AutcomInfo(string? FileVersion, string? ProductVersion, double? SizeMb, string Source, string? Error);

/// <summary>Regras de negocio da copia (Copiar Local / Copiar Cloud), portadas do VarreduraSistema.</summary>
public static class CopyRules
{
    public const int LocalMaxAutcomMb = 100;
    public const int CloudMinAutcomMb = 150;

    /// <summary>Destinos padrao onde a pasta da versao e procurada.</summary>
    public const string IndividualRoot = @"\\citel-fileserve\Modem\_VERSOES_INDIVIDUAIS";
    public static readonly string[] DefaultTargetRoots =
    {
        @"\\citel-fileserve\Modem\VERSOES\379_Versoes\379.48.02\379.48.2.30",
        IndividualRoot
    };

    public static bool IsCloud(string folderName) => ClosingService.IsCloud(folderName);

    public static string TitleFor(bool cloud) => cloud ? "Copiar Cloud" : "Copiar Local";

    private static string BaseName(string folderName) =>
        folderName.EndsWith("_CLOUD", StringComparison.OrdinalIgnoreCase) ? folderName[..^6] : folderName;

    /// <summary>Copia especifica: nome com 5 partes numericas (ex.: 379.48.2.40.5).</summary>
    public static bool IsSpecificCopy(string folderName)
    {
        var parts = BaseName(folderName).Split('.');
        return parts.Length == 5 && parts.All(p => p.Length > 0 && p.All(c => c is >= '0' and <= '9'));
    }

    /// <summary>
    /// FileVersion e ProductVersion esperados pelo nome da pasta: 379.48.2.30.74.150 -> 48.02.30.74 / 02.30.74.150;
    /// copia especifica 379.48.2.40.5 -> 379.48.02.40 / 48.02.40.05. Null se o nome nao segue o padrao.
    /// </summary>
    public static (string File, string Product)? ExpectedVersions(string folderName)
    {
        var parts = BaseName(folderName).Split('.');
        var specific = IsSpecificCopy(folderName);
        if (parts.Length < 6 && !specific) return null;

        var normalized = parts.Select(p => p.Length == 1 && p[0] is >= '0' and <= '9' ? "0" + p : p).ToArray();
        return specific
            ? (string.Join('.', normalized[0..4]), string.Join('.', normalized[1..5]))
            : (string.Join('.', normalized[1..5]), string.Join('.', normalized[2..6]));
    }

    /// <summary>Nomes de pasta aceitos no destino: o do projeto e, na copia Local, tambem o com sufixo _LOCAL.</summary>
    public static List<string> DestinationNames(string folderName, bool cloud)
    {
        var names = new List<string> { folderName };
        if (!cloud) names.Add(folderName + "_LOCAL");
        return names;
    }

    /// <summary>
    /// Pasta a criar em _VERSOES_INDIVIDUAIS quando o destino nao existe: subpasta = ProductVersion com hifen antes da
    /// ultima parte (02.30.74.150 -> 02.30.74-150); pasta final = nome do projeto, com _LOCAL na copia Local.
    /// Copias especificas nao tem regra conhecida: null.
    /// </summary>
    public static string? NewDestinationFor(string folderName, bool cloud, string? individualRoot = null)
    {
        if (IsSpecificCopy(folderName)) return null;
        var versions = ExpectedVersions(folderName);
        if (versions is null) return null;
        var parts = versions.Value.Product.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0)) return null;

        var versionFolder = $"{string.Join('.', parts[..3])}-{parts[3]}";
        var finalName = cloud ? folderName : folderName + "_LOCAL";
        return Path.Combine(individualRoot ?? IndividualRoot, versionFolder, finalName);
    }

    /// <summary>
    /// Confere o texto digitado como destino de copia extra: tira aspas, espacos e barras finais, exige caminho completo
    /// (letra de unidade ou \\servidor\pasta) e recusa o que ja esta na lista ou entre os destinos padrao.
    /// </summary>
    /// <returns>O caminho limpo, ou null com o motivo em <c>Error</c>.</returns>
    public static (string? Value, string? Error) NormalizeExtraTarget(string? text, IEnumerable<string> existing)
    {
        var value = (text ?? "").Trim().Trim('"').Trim();
        if (value.Length == 0) return (null, "Informe o caminho da pasta.");

        var isUnc = value.StartsWith(@"\\", StringComparison.Ordinal) && value.Length > 2;
        var isDrive = value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');
        if (!isUnc && !isDrive)
            return (null, @"Use o caminho completo, como \\servidor\pasta ou D:\pasta.");

        value = value.TrimEnd('\\', '/');
        if (isDrive && value.Length <= 2) return (null, "Informe uma pasta, não a raiz do disco.");

        if (existing.Any(e => string.Equals(e.TrimEnd('\\', '/'), value, StringComparison.OrdinalIgnoreCase)))
            return (null, "Esse destino já está na lista (ou é um dos destinos padrão).");
        return (value, null);
    }

    public static bool LocalCopyAllowed(double? autcomMb) => autcomMb is { } mb && mb < LocalMaxAutcomMb;
    public static bool CloudCopyAllowed(double? autcomMb) => autcomMb is { } mb && mb >= CloudMinAutcomMb;

    public static bool IsInside(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Le o Autcom.exe da pasta do projeto, ou de dentro do Autcom.zip (depois do fechamento so restam os ZIPs).
    /// Sempre do disco, na hora: nada de resultado de varredura antiga.
    /// </summary>
    public static AutcomInfo ReadAutcom(string projectPath)
    {
        try
        {
            var exe = Path.Combine(projectPath, "Autcom.exe");
            if (File.Exists(exe))
            {
                var v = VersionReader.Read(exe);
                return new AutcomInfo(v?.File, v?.Product, new FileInfo(exe).Length / 1024d / 1024d, "Pasta", null);
            }

            var zipPath = Directory.EnumerateFiles(projectPath, "*.zip")
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "Autcom.zip", StringComparison.OrdinalIgnoreCase));
            if (zipPath is null) return new AutcomInfo(null, null, null, "Ausente", "Autcom.exe nao encontrado na pasta nem em Autcom.zip");

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "Autcom.exe", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return new AutcomInfo(null, null, null, "Zip", "Autcom.exe ausente no zip");

            var temp = Path.Combine(Path.GetTempPath(), "monitorsvn-autcom-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var extracted = Path.Combine(temp, "Autcom.exe");
                entry.ExtractToFile(extracted);
                var v = VersionReader.Read(extracted);
                return new AutcomInfo(v?.File, v?.Product, entry.Length / 1024d / 1024d, "Zip", null);
            }
            finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new AutcomInfo(null, null, null, "Erro", ex is InvalidDataException ? "Zip invalido" : ex.Message);
        }
    }

    public sealed record Check(string Label, bool Ok, string Detail);

    /// <summary>Validacoes de seguranca mostradas antes de copiar. Todas precisam estar OK para liberar o botao.</summary>
    public static List<Check> SafetyChecks(string projectPath, string destination, IReadOnlyCollection<string> targetRoots, bool cloud,
        ProjectAnalyzer? analyzer = null)
    {
        var name = Path.GetFileName(projectPath.TrimEnd('\\', '/'));
        var checks = new List<Check>();
        void Add(string label, bool ok, string detail) => checks.Add(new Check(label, ok, detail));

        var projectCloud = IsCloud(name);
        Add("Tipo do projeto", cloud == projectCloud, $"acao={(cloud ? "Cloud" : "Local")}; projeto={(projectCloud ? "Cloud" : "Local")}");

        // no lugar do "status OK" do VarreduraSistema: a pasta so tem os ZIPs (o fechamento terminou)
        var pending = Directory.Exists(projectPath) ? ClosingService.PendingFiles(projectPath) : new List<string>();
        var zipCount = Directory.Exists(projectPath) ? Directory.EnumerateFiles(projectPath, "*.zip").Count() : 0;
        Add("Fechamento concluído", pending.Count == 0 && zipCount > 0,
            pending.Count > 0 ? $"{pending.Count} arquivo(s) ainda nao compactado(s): {string.Join(", ", pending.Take(3))}"
            : zipCount == 0 ? "nenhum ZIP na pasta" : $"{zipCount} ZIP(s)");

        if (analyzer is not null && Directory.Exists(projectPath))
        {
            // o "Status OK": todos os arquivos do padrao da versao presentes e com a versao certa
            var analysis = analyzer.Analyze(projectPath);
            Add("Status do projeto", analysis.Ok, analysis.Status);
        }

        var expectedNames = DestinationNames(name, cloud);
        var destName = Path.GetFileName(destination.TrimEnd('\\', '/'));
        Add("Nome do destino", expectedNames.Contains(destName, StringComparer.OrdinalIgnoreCase),
            $"destino={destName}; esperado={string.Join(" ou ", expectedNames)}");
        Add("Destino dentro da raiz configurada", targetRoots.Any(r => IsInside(destination, r)), string.Join("; ", targetRoots));

        var expected = ExpectedVersions(name);
        var autcom = ReadAutcom(projectPath);
        Add("FileVersion", autcom.FileVersion is not null && expected is not null && autcom.FileVersion == expected.Value.File,
            $"atual={autcom.FileVersion ?? "vazio"}; esperado={expected?.File ?? "vazio"}");
        Add("ProductVersion", autcom.ProductVersion is not null && expected is not null && autcom.ProductVersion == expected.Value.Product,
            $"atual={autcom.ProductVersion ?? "vazio"}; esperado={expected?.Product ?? "vazio"}");
        Add("Autcom MB", autcom.SizeMb is not null, autcom.SizeMb is { } mb ? $"{mb:0.00} MB" : (autcom.Error ?? "vazio"));
        if (cloud) Add("Limite Cloud", CloudCopyAllowed(autcom.SizeMb), $"Autcom a partir de {CloudMinAutcomMb} MB");
        else Add("Limite Local", LocalCopyAllowed(autcom.SizeMb), $"Autcom abaixo de {LocalMaxAutcomMb} MB");

        var zipErrors = Directory.Exists(projectPath) ? ZipNameValidator.Validate(projectPath) : new List<string> { "pasta nao encontrada" };
        Add("Nome dos ZIPs", zipErrors.Count == 0, zipErrors.Count == 0 ? "OK" : $"{zipErrors.Count} erro(s): {string.Join("; ", zipErrors.Take(2))}");
        return checks;
    }
}
