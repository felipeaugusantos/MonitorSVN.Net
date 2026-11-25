namespace MonitorSVN.Services;

/// <summary>
/// Cada versao gerada tem duas pastas irmas em "pasta raiz de geracao": a Local (<c>379.48.2.30.74.150</c>) e a Cloud
/// (<c>379.48.2.30.74.150_CLOUD</c>). O Monitor tem uma linha so por versao, entao as duas sao tratadas a partir dela.
/// </summary>
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
    public static string ComposeColumn(string local, string cloud)
    {
        if (local.Length == 0 && cloud.Length == 0) return "";
        if (cloud.Length == 0) return local;
        if (local.Length == 0) return "Cloud: " + cloud;
        return $"Local: {local} · Cloud: {cloud}";
    }
}
