namespace MonitorSVN.Services;

/// <summary>
/// Cada versao gerada tem duas pastas irmas em "pasta raiz de geracao": a Local (<c>379.48.2.30.74.150</c>) e a Cloud
/// (<c>379.48.2.30.74.150_CLOUD</c>). O Monitor tem uma linha so por versao, entao as duas sao tratadas a partir dela.
/// </summary>
/// <summary>Missing = nao existe; Empty = existe sem arquivos; Open = tem arquivos, ainda nao fechada; Closed = fechada (tudo em ZIP).</summary>
public enum FolderState { Missing, Empty, Open, Closed }

public static class VersionFolders
{
    private const string CloudSuffix = "_CLOUD";

    /// <summary>Nome da versao sem o sufixo _CLOUD (a linha pode, em tese, ja trazê-lo).</summary>
    public static string BaseName(string version) =>
        version.EndsWith(CloudSuffix, StringComparison.OrdinalIgnoreCase) ? version[..^CloudSuffix.Length] : version;

    public static string FolderName(string version, bool cloud) => BaseName(version) + (cloud ? CloudSuffix : "");

    public static string PathFor(string generationRoot, string version, bool cloud) =>
        Path.Combine(generationRoot, FolderName(version, cloud));

    /// <summary>
    /// Texto da coluna Fechamento com as duas pastas. Uma so tem situacao: mostra o texto dela (como sempre foi);
    /// as duas: "Local: ... · Cloud: ...".
    /// </summary>
    /// <summary>
    /// Situacao de uma pasta: so e <see cref="FolderState.Closed"/> quando existe, nao tem mais .exe/.dll/.bpl soltos
    /// (passou pelo fechamento), tem ZIPs e nenhum fechamento esta rodando nela.
    /// </summary>
    public static FolderState StateOf(bool exists, int pendingFiles, int zipFiles, bool closingActive)
    {
        if (!exists) return FolderState.Missing;
        if (pendingFiles == 0 && zipFiles == 0) return FolderState.Empty;
        return pendingFiles == 0 && !closingActive ? FolderState.Closed : FolderState.Open;
    }

    public static FolderState StateOf(string folder, bool closingActive)
    {
        if (!Directory.Exists(folder)) return FolderState.Missing;
        try
        {
            var zips = Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly).Count();
            return StateOf(true, ClosingService.PendingFiles(folder).Count, zips, closingActive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return FolderState.Open; }
    }

    public static string StateText(FolderState state) => state switch
    {
        FolderState.Closed => "fechada: tudo em ZIP",
        FolderState.Open => "tem os arquivos, ainda não fechada",
        FolderState.Empty => "pasta vazia",
        _ => "pasta não encontrada"
    };

    public static string ComposeColumn(string local, string cloud)
    {
        if (local.Length == 0 && cloud.Length == 0) return "";
        if (cloud.Length == 0) return local;
        if (local.Length == 0) return "Cloud: " + cloud;
        return $"Local: {local}{Environment.NewLine}Cloud: {cloud}";
    }
}
