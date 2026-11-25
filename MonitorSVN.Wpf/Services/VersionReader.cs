using System.Diagnostics;

namespace MonitorSVN.Services;

/// <summary>
/// Le FileVersion e ProductVersion de um .exe/.dll (campos numericos do recurso de versao do Windows), no mesmo
/// formato do VarreduraSistema: 4 partes, as menores que 10 com zero a esquerda (48.02.30.74).
/// </summary>
public static class VersionReader
{
    public readonly record struct Versions(string File, string Product);

    /// <returns>null se o arquivo nao tiver recurso de versao.</returns>
    public static Versions? Read(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileVersion is null && info.ProductVersion is null) return null;

        return new Versions(
            Format(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart),
            Format(info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart));
    }

    public static string Format(int a, int b, int c, int d) =>
        string.Join('.', new[] { a, b, c, d }.Select(p => p < 10 ? p.ToString("00") : p.ToString()));
}
