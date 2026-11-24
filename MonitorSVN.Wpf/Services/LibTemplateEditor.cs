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

    /// <summary>
    /// Desfaz <see cref="AddToBat"/>: tira o "SET Exec...=0" e o bloco "IF %Exec...% EQU 0 ( ... )" da lib.
    /// Recusa se o bloco nao tiver o formato conhecido, para nunca apagar nada de outra lib.
    /// </summary>
    public static string RemoveFromBat(string bat, string name)
    {
        ValidateName(name);
        var n = Regex.Escape(name);
        var setLine = Regex.Match(bat, $@"(?im)^[ \t]*SET[ \t]+Exec{n}[ \t]*=0[ \t]*\r?\n");
        var blocks = Regex.Matches(bat,
            $@"(?im)^IF[ \t]+%Exec{n}%[ \t]+EQU[ \t]+0[ \t]+\(\r?\n(?:[^\r\n]*\r?\n)*?\)[ \t]*(?:\r?\n|$)(?:[ \t]*\r?\n)?");

        if (!setLine.Success && blocks.Count == 0)
            throw new LibEditException($"{name} nao esta no _FechamentoArquivos.bat.");
        if (blocks.Count > 1)
            throw new LibEditException($"_FechamentoArquivos.bat: {name} aparece em mais de um bloco; nao sei qual remover.");

        var sb = new StringBuilder(bat);
        if (blocks.Count == 1)
        {
            var b = blocks[0];
            // o bloco tem de ser so desta lib: o .cmd dela e mais nenhuma variavel Exec... alem da propria
            var otherVars = Regex.Matches(b.Value, @"(?i)Exec[A-Za-z0-9_]+").Select(m => m.Value)
                .Where(v => !v.Equals("Exec" + name, StringComparison.OrdinalIgnoreCase));
            if (!Regex.IsMatch(b.Value, $@"comandosCMD\\{n}\.cmd", RegexOptions.IgnoreCase) || otherVars.Any())
                throw new LibEditException($"_FechamentoArquivos.bat: o bloco de {name} esta fora do formato esperado; remova a mao.");
            sb.Remove(b.Index, b.Length);   // posicao maior primeiro: nao desloca a linha do SET
        }
        if (setLine.Success) sb.Remove(setLine.Index, setLine.Length);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ _ComandosBKP.txt / comando do Jenkins

    /// <summary>
    /// Desfaz <see cref="AddToBkp"/>: tira as 4 linhas da lib (CALL, IF, madExceptPatch, copy) e a linha em branco
    /// que as antecede. Recusa se as 4 linhas nao forem as esperadas.
    /// </summary>
    public static string RemoveFromBkp(string bkp, string name)
    {
        ValidateName(name);
        var n = Regex.Escape(name);
        var found = Regex.Matches(bkp,
            $@"(?im)^CALL[^\r\n]*ddcc32NovoDX10\.cmd[ \t]+{n}[ \t][^\r\n]*\r?\n(?<l2>[^\r\n]*)\r?\n(?<l3>[^\r\n]*)\r?\n(?<l4>[^\r\n]*)(?:\r?\n|$)");
        if (found.Count == 0) throw new LibEditException($"{name} nao esta nos comandos de compilacao.");
        if (found.Count > 1) throw new LibEditException($"{name} aparece mais de uma vez nos comandos de compilacao; nao sei qual remover.");

        var m = found[0];
        var ok = Regex.IsMatch(m.Groups["l2"].Value, @"^\s*IF NOT %ERRORLEVEL% == 0 GOTO END\s*$", RegexOptions.IgnoreCase) &&
                 m.Groups["l3"].Value.Contains("madExceptPatch.exe", StringComparison.OrdinalIgnoreCase) &&
                 m.Groups["l3"].Value.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                 Regex.IsMatch(m.Groups["l4"].Value, @"^\s*copy /Y\b", RegexOptions.IgnoreCase) &&
                 m.Groups["l4"].Value.Contains(name, StringComparison.OrdinalIgnoreCase);
        if (!ok)
            throw new LibEditException($"Comandos de {name} fora do formato esperado (4 linhas: CALL, IF, madExceptPatch, copy); remova a mao.");

        var start = m.Index;
        var blankBefore = Regex.Match(bkp[..start], @"(?<=(?:^|\n))[ \t]*\r?\n\z");
        if (blankBefore.Success) start = blankBefore.Index;
        return bkp.Remove(start, m.Index + m.Length - start);
    }

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
    public static string AddToJobConfig(string configXml, LibSpec lib) =>
        EditJobCommand(configXml, command => AddToBkp(command, lib));

    /// <summary>Desfaz <see cref="AddToJobConfig"/> no passo "Executar comando batch" do job.</summary>
    public static string RemoveFromJobConfig(string configXml, string name) =>
        EditJobCommand(configXml, command => RemoveFromBkp(command, name));

    private static string EditJobCommand(string configXml, Func<string, string> edit)
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
        var updated = edit(WebUtility.HtmlDecode(body.Value));
        return configXml.Remove(body.Index, body.Length).Insert(body.Index, EscapeXmlText(updated));
    }

    private static string EscapeXmlText(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#xd;");

    public static bool JobConfigHas(string configXml, string name) =>
        CommandElement.Matches(configXml).Cast<Match>()
            .Any(m => BkpHas(WebUtility.HtmlDecode(m.Groups["body"].Value), name));
}
