using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MonitorSVN.Services;

/// <summary>De onde sai e para onde vai a DLL no passo de compilacao (_ComandosBKP.txt / job do Jenkins).</summary>
public sealed record LibSpec(string Name, string SourceDir, string OutputDir)
{
    public string DllPath => OutputDir.TrimEnd('\\') + "\\" + Name + ".dll";

    public static LibSpec Cartoes(string name) =>
        new(name, $@"%WORKSPACE%\libCartoes\{name}\", $@"%WORKSPACE%\libCartoes\{name}\");

    public static LibSpec Boletos(string name) =>
        new(name, $@"%WORKSPACE%\libBoletos\{name}\", $@"%WORKSPACE%\libBoletos\{name}\");

    public static LibSpec Tintas(string name) =>
        new(name, $@"%WORKSPACE%\LibTintas\{name}\", @"%WORKSPACE%\padra_atualizado\auttin\tintas");
}

/// <summary>Falha esperada (arquivo fora do formato conhecido, lib ja existente...): nada e gravado.</summary>
public sealed class LibEditException(string message) : Exception(message);

/// <summary>
/// Edicoes de texto puras sobre os arquivos de um modelo: _FechamentoArquivos.bat, _ComandosBKP.txt (que e a mesma
/// sequencia de comandos do job-modelo do Jenkins) e o .cmd de cada lib. Nada aqui toca em disco ou na rede.
/// </summary>
public static class LibTemplateEditor
{
    private static readonly Regex NameRule = new(@"^[A-Za-z][A-Za-z0-9_]{2,80}$", RegexOptions.Compiled);

    public static void ValidateName(string name)
    {
        if (!NameRule.IsMatch(name))
            throw new LibEditException("Nome da lib invalido. Use apenas letras, numeros e _ (ex.: libCartoesNovo).");
    }

    public static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    // ------------------------------------------------------------------ leitura

    /// <summary>Libs registradas no _FechamentoArquivos.bat (as que tem .cmd em comandosCMD).</summary>
    public static List<string> ListLibsInBat(string bat) =>
        Regex.Matches(bat, @"comandosCMD\\(?<n>[A-Za-z0-9_]+)\.cmd", RegexOptions.IgnoreCase)
            .Select(m => m.Groups["n"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool BatHas(string bat, string name) =>
        Regex.IsMatch(bat, $@"(?im)^\s*SET\s+Exec{Regex.Escape(name)}\s*=") ||
        Regex.IsMatch(bat, $@"comandosCMD\\{Regex.Escape(name)}\.cmd", RegexOptions.IgnoreCase);

    public static bool BkpHas(string bkp, string name) =>
        Regex.IsMatch(bkp, $@"ddcc32NovoDX10\.cmd\s+{Regex.Escape(name)}(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // ------------------------------------------------------------------ _FechamentoArquivos.bat

    public static string AddToBat(string bat, string name)
    {
        ValidateName(name);
        if (BatHas(bat, name)) throw new LibEditException($"{name} ja existe no _FechamentoArquivos.bat.");

        var nl = NewLine(bat);
        var variable = "Exec" + name;

        var start = Regex.Match(bat, @"(?m)^:START[ \t]*\r?$");
        var tempo = Regex.Match(bat, @"(?m)^echo Tempo decorrido");
        if (!start.Success || !tempo.Success)
            throw new LibEditException("_FechamentoArquivos.bat fora do formato esperado (nao achei ':START' ou 'echo Tempo decorrido').");

        // 1) declaracao: logo depois da ultima linha "SET Exec...=0" antes de :START
        var lastSet = Regex.Matches(bat, @"(?m)^SET Exec\S*=0[ \t]*\r?\n")
            .Cast<Match>().LastOrDefault(m => m.Index < start.Index)
            ?? throw new LibEditException("_FechamentoArquivos.bat: nao achei o bloco de 'SET Exec...=0'.");

        // 2) bloco de execucao: antes do 'echo Tempo decorrido' (posicao maior, entao e inserido primeiro)
        var block = string.Join(nl, new[]
        {
            $"IF %{variable}% EQU 0 (",
            $"\tdir \"C:\\VERSOES_FECHADAS\\%versao%\\{name}.dll\" > nul 2>&1",
            "\t",
            "\tIF NOT ERRORLEVEL 1 (",
            $"\t\tstart /MIN C:\\VERSOES_FECHADAS\\%versao%\\comandosCMD\\{name}.cmd",
            $"\t\tset {variable}=1",
            "\t)",
            ")",
            "", ""
        });

        var sb = new StringBuilder(bat);
        sb.Insert(tempo.Index, block);
        sb.Insert(lastSet.Index + lastSet.Length, $"SET {variable}=0{nl}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ _ComandosBKP.txt / comando do Jenkins

    public static string AddToBkp(string bkp, LibSpec lib)
    {
        ValidateName(lib.Name);
        if (BkpHas(bkp, lib.Name)) throw new LibEditException($"{lib.Name} ja existe nos comandos de compilacao.");
        if (string.IsNullOrWhiteSpace(lib.SourceDir) || string.IsNullOrWhiteSpace(lib.OutputDir))
            throw new LibEditException("Informe a pasta do projeto e a pasta de saida da lib.");

        var done = Regex.Match(bkp, @"(?m)^echo concluido\b");
        if (!done.Success)
            throw new LibEditException("Comandos de compilacao fora do formato esperado (nao achei 'echo concluido').");

        var nl = NewLine(bkp);
        var lines = new[]
        {
            $@"CALL C:\Compilador\ddcc32NovoDX10.cmd {lib.Name} {lib.SourceDir} {lib.OutputDir}",
            "IF NOT %ERRORLEVEL% == 0 GOTO END",
            $@"C:\Compilador\madExceptPatch.exe {lib.DllPath}",
            $@"copy /Y {lib.DllPath} C:\VERSOES_FECHADAS\releaseversion"
        };

        // Mesma familia (libCartoes*, libBoletos*, libTinta*): entra junto das outras, no fim da secao.
        var family = Regex.Match(lib.Name, @"^lib(Cartoes|Boletos|Tinta)", RegexOptions.IgnoreCase);
        if (family.Success)
        {
            var sameFamily = Regex.Matches(bkp,
                    $@"(?im)^CALL[^\r\n]*ddcc32NovoDX10\.cmd\s+{family.Value}\w*\s[^\r\n]*\r?\n[^\r\n]*\r?\n[^\r\n]*\r?\n[^\r\n]*(\r?\n|$)")
                .Cast<Match>()
                .Where(m => m.Index < done.Index)
                .LastOrDefault();
            if (sameFamily is not null)
                return bkp.Insert(sameFamily.Index + sameFamily.Length, nl + string.Join(nl, lines) + nl);
        }

        // Sem secao conhecida: antes do 'echo concluido' (e portanto antes da copia CLOUD, que vem depois dele)
        return bkp.Insert(done.Index, string.Join(nl, lines) + nl + nl);
    }

    // ------------------------------------------------------------------ .cmd da lib

    /// <summary>Cria o .cmd da lib nova a partir de um .cmd existente, trocando apenas o nome da lib.</summary>
    public static string CmdFromReference(string referenceCmd, string referenceName, string name)
    {
        ValidateName(name);
        if (!referenceCmd.Contains(referenceName, StringComparison.OrdinalIgnoreCase))
            throw new LibEditException($"O .cmd de referencia nao menciona {referenceName}.");
        return Regex.Replace(referenceCmd, Regex.Escape(referenceName), name.Replace("$", "$$"), RegexOptions.IgnoreCase);
    }

    // ------------------------------------------------------------------ config.xml do Jenkins

    private static readonly Regex CommandElement =
        new(@"<command>(?<body>.*?)</command>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Aplica <see cref="AddToBkp"/> ao passo "Executar comando batch" do job-modelo. So mexe no unico
    /// elemento &lt;command&gt; que contem 'echo concluido'; o resto do XML e preservado byte a byte.
    /// </summary>
    public static string AddToJobConfig(string configXml, LibSpec lib)
    {
        var targets = CommandElement.Matches(configXml)
            .Cast<Match>()
            .Where(m => WebUtility.HtmlDecode(m.Groups["body"].Value).Contains("echo concluido", StringComparison.Ordinal))
            .ToList();

        if (targets.Count != 1)
            throw new LibEditException(targets.Count == 0
                ? "Job-modelo do Jenkins: nao achei o comando que contem 'echo concluido'."
                : "Job-modelo do Jenkins: ha mais de um comando com 'echo concluido'; nao sei qual alterar.");

        var body = targets[0].Groups["body"];
        var updated = AddToBkp(WebUtility.HtmlDecode(body.Value), lib);
        return configXml.Remove(body.Index, body.Length).Insert(body.Index, EscapeXmlText(updated));
    }

    private static string EscapeXmlText(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#xd;");

    public static bool JobConfigHas(string configXml, string name) =>
        CommandElement.Matches(configXml).Cast<Match>()
            .Any(m => BkpHas(WebUtility.HtmlDecode(m.Groups["body"].Value), name));
}
