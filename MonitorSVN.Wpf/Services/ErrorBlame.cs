using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MonitorSVN.Services;

/// <summary>Descobre, a partir da mensagem de erro do compilador, quem alterou o arquivo citado.</summary>
public static class ErrorBlame
{
    private static readonly Regex UnitQuoted = new(@"unit\s+'(?<f>[^']+)'", RegexOptions.IgnoreCase);
    private static readonly Regex AnyFile = new(@"(?<f>[\w.\-]+\.(?:pas|dpr|dfm|inc))\b", RegexOptions.IgnoreCase);

    /// <summary>Nome do arquivo citado no erro (ex.: untGarantiaEnvFornecedor.pas) ou null.</summary>
    public static string? ExtractFile(string? errorDetail)
    {
        if (string.IsNullOrWhiteSpace(errorDetail)) return null;
        var m = UnitQuoted.Match(errorDetail);
        if (!m.Success) m = AnyFile.Match(errorDetail);
        if (!m.Success) return null;

        var name = m.Groups["f"].Value.Trim();
        return name.Contains('.') ? name : name + ".pas";
    }

    /// <summary>true se o XML de "svn log -v" desta revisão tem um caminho terminando em /fileName.</summary>
    public static bool Touches(string xml, string fileName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        var suffix = "/" + fileName;
        return XDocument.Parse(xml).Descendants("path")
            .Any(p => p.Value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
