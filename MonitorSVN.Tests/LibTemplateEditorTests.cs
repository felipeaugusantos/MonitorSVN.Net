using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class LibTemplateEditorTests
{
    private const string Bat =
        "@echo off\r\n\r\nSET ExeclibCartoesSipag=0\r\nSET ExeclibBoletosItau=0\r\n\r\n:: DLL de tintas\r\nSET ExecAdvance=0\r\n\r\n:START\r\n\r\n" +
        "IF %ExeclibBoletosItau% EQU 0 (\r\n\tdir \"C:\\VERSOES_FECHADAS\\%versao%\\libBoletosItau.dll\" > nul 2>&1\r\n\t\r\n\tIF NOT ERRORLEVEL 1 (\r\n" +
        "\t\tstart /MIN C:\\VERSOES_FECHADAS\\%versao%\\comandosCMD\\libBoletosItau.cmd\r\n\t\tset ExeclibBoletosItau=1\r\n\t)\r\n)\r\n\r\n" +
        "IF %ExeclibCartoesSipag% EQU 0 (\r\n\tdir \"C:\\VERSOES_FECHADAS\\%versao%\\libCartoesSipag.dll\" > nul 2>&1\r\n\t\r\n\tIF NOT ERRORLEVEL 1 (\r\n" +
        "\t\tstart /MIN C:\\VERSOES_FECHADAS\\%versao%\\comandosCMD\\libCartoesSipag.cmd\r\n\t\tset ExeclibCartoesSipag=1\r\n\t)\r\n)\r\n\r\n" +
        "echo Tempo decorrido: %ELAPSED_TIME%\r\n\r\ngoto START";

    private const string Bkp =
        "del C:\\VERSOES_FECHADAS\\releaseversion\\*.* /q\r\n\r\n" +
        "CALL C:\\Compilador\\ddcc32NovoDX10.cmd libCartoesSipag %WORKSPACE%\\libCartoes\\libCartoesSipag\\ %WORKSPACE%\\libCartoes\\libCartoesSipag\\\r\n" +
        "IF NOT %ERRORLEVEL% == 0 GOTO END\r\n\r\n\r\necho concluido > C:\\VERSOES_FECHADAS\\releaseversion\\concluido.txt\r\n\r\n:END\r\n";

    private const string Cmd =
        "C:\\Compilador\\ResourceHacker.exe -open \"C:\\VERSOES_FECHADAS\\releaseversion\\libCartoesCielo.dll\" -save \"C:\\VERSOES_FECHADAS\\releaseversion\\libCartoesCielo.dll\"\r\n" +
        "C:\\Compilador\\signtool.exe sign \"C:\\VERSOES_FECHADAS\\releaseversion\\libCartoesCielo.dll\"\r\n" +
        "move C:\\VERSOES_FECHADAS\\releaseversion\\libCartoesCielo.dll C:\\VERSOES_FECHADAS\\releaseversion\\err_libCartoesCielo.dll\r\n";

    [Fact]
    public void Lists_libs_from_the_bat()
    {
        Assert.Equal(new[] { "libBoletosItau", "libCartoesSipag" }, LibTemplateEditor.ListLibsInBat(Bat));
    }

    [Fact]
    public void Bat_gets_declaration_and_execution_block_and_keeps_everything_else()
    {
        var result = LibTemplateEditor.AddToBat(Bat, "libCartoesNovo");

        Assert.Contains("SET ExeclibCartoesNovo=0\r\n", result);
        Assert.Contains("IF %ExeclibCartoesNovo% EQU 0 (", result);
        Assert.Contains(@"comandosCMD\libCartoesNovo.cmd", result);
        Assert.Contains("set ExeclibCartoesNovo=1", result);

        // declaracao antes de :START, bloco de execucao antes do 'echo Tempo decorrido'
        Assert.True(result.IndexOf("SET ExeclibCartoesNovo=0") < result.IndexOf(":START"));
        Assert.True(result.IndexOf("IF %ExeclibCartoesNovo% EQU 0") < result.IndexOf("echo Tempo decorrido"));

        // so acrescenta: tirando o que entrou, volta exatamente o original
        var restored = result.Replace("SET ExeclibCartoesNovo=0\r\n", "");
        var blockStart = restored.IndexOf("IF %ExeclibCartoesNovo% EQU 0");
        var blockEnd = restored.IndexOf("echo Tempo decorrido");
        Assert.Equal(Bat, restored.Remove(blockStart, blockEnd - blockStart));

        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));   // nenhum LF solto: segue CRLF
    }

    [Fact]
    public void Bat_rejects_a_lib_that_already_exists_case_insensitively()
    {
        Assert.Throws<LibEditException>(() => LibTemplateEditor.AddToBat(Bat, "libcartoessipag"));
    }

    [Fact]
    public void Bat_without_known_anchors_is_rejected()
    {
        Assert.Throws<LibEditException>(() => LibTemplateEditor.AddToBat("@echo off\r\n", "libCartoesNovo"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lib Nova")]
    [InlineData("lib;rm")]
    [InlineData("1abc")]
    [InlineData("a")]
    public void Invalid_names_are_rejected(string name)
    {
        Assert.Throws<LibEditException>(() => LibTemplateEditor.AddToBat(Bat, name));
    }

    [Fact]
    public void Bkp_gets_the_compile_block_before_the_concluido_line()
    {
        var result = LibTemplateEditor.AddToBkp(Bkp, LibSpec.Cartoes("libCartoesNovo"));

        Assert.Contains(@"CALL C:\Compilador\ddcc32NovoDX10.cmd libCartoesNovo %WORKSPACE%\libCartoes\libCartoesNovo\ %WORKSPACE%\libCartoes\libCartoesNovo\", result);
        Assert.Contains(@"C:\Compilador\madExceptPatch.exe %WORKSPACE%\libCartoes\libCartoesNovo\libCartoesNovo.dll", result);
        Assert.Contains(@"copy /Y %WORKSPACE%\libCartoes\libCartoesNovo\libCartoesNovo.dll C:\VERSOES_FECHADAS\releaseversion", result);
        Assert.True(result.IndexOf("libCartoesNovo.dll") < result.IndexOf("echo concluido"));
        Assert.EndsWith(":END\r\n", result);
    }

    // formato do _ComandosBKP.txt atual: secoes por familia e passo CLOUD depois do 'echo concluido'
    private static string Block(string name, string src, string? output = null) =>
        $"CALL C:\\Compilador\\ddcc32NovoDX10.cmd {name} {src} {output ?? src}\r\nIF NOT %ERRORLEVEL% == 0 GOTO END\r\n" +
        $"C:\\Compilador\\madExceptPatch.exe {(output ?? src)}{name}.dll\r\ncopy /Y {(output ?? src)}{name}.dll C:\\VERSOES_FECHADAS\\releaseversion\r\n";

    private static readonly string Sectioned =
        "del x\r\n\r\n" +
        ":: DLL's DE libCartoes ----\r\n\r\n" +
        Block("libCartoesA", @"%WORKSPACE%\libCartoes\libCartoesA\") + "\r\n" +
        Block("libCartoesB", @"%WORKSPACE%\libCartoes\libCartoesB\") + "\r\n" +
        ":: DLL's DE libBoletos ----\r\n\r\n" +
        Block("libBoletosA", @"%WORKSPACE%\libBoletos\libBoletosA\") + "\r\n" +
        ":: DLL's DE TINTAS ----\r\n" +
        Block("libTintaA", @"%WORKSPACE%\LibTintas\libTintaA\", @"%WORKSPACE%\padra_atualizado\auttin\tintas\") + "\r\n" +
        "\r\necho concluido > C:\\VERSOES_FECHADAS\\releaseversion\\concluido.txt\r\n\r\n" +
        ":: Copia a pasta completa para a versao CLOUD\r\nrd /s /q \"C:\\VERSOES_FECHADAS\\releaseversion_CLOUD\"\r\n" +
        "powershell -NoProfile -Command \"$c = $c -replace '(?m)^(?!::)(.*upx\\.exe.*)$','::$1'; exit 0\"\r\n\r\n:END\r\n";

    [Theory]
    [InlineData("libCartoesNovo", "libCartoesB")]
    [InlineData("libBoletosNovo", "libBoletosA")]
    [InlineData("libTintaNova", "libTintaA")]
    public void Bkp_puts_the_new_lib_right_after_the_last_one_of_its_family(string name, string lastOfFamily)
    {
        var spec = name.StartsWith("libCartoes") ? LibSpec.Cartoes(name)
                 : name.StartsWith("libBoletos") ? LibSpec.Boletos(name)
                 : LibSpec.Tintas(name);

        var result = LibTemplateEditor.AddToBkp(Sectioned, spec);

        var iLast = result.IndexOf($"ddcc32NovoDX10.cmd {lastOfFamily} ");
        var iNew = result.IndexOf($"ddcc32NovoDX10.cmd {name} ");
        Assert.True(iLast >= 0 && iNew > iLast, "a nova lib deve vir depois da ultima da familia");

        // nao invade a secao seguinte
        var nextSection = name.StartsWith("libCartoes") ? "DE libBoletos"
                        : name.StartsWith("libBoletos") ? "DE TINTAS"
                        : "echo concluido";
        Assert.True(iNew < result.IndexOf(nextSection));

        // e o resto do arquivo (inclusive o passo CLOUD e o powershell) segue identico
        Assert.Contains("rd /s /q \"C:\\VERSOES_FECHADAS\\releaseversion_CLOUD\"", result);
        Assert.Contains("(?m)^(?!::)(.*upx\\.exe.*)$", result);
        // so acrescenta: removido o bloco inserido, sobra exatamente o arquivo original
        var inserted = "\r\n" + string.Join("\r\n", new[]
        {
            $@"CALL C:\Compilador\ddcc32NovoDX10.cmd {name} {spec.SourceDir} {spec.OutputDir}",
            "IF NOT %ERRORLEVEL% == 0 GOTO END",
            $@"C:\Compilador\madExceptPatch.exe {spec.DllPath}",
            $@"copy /Y {spec.DllPath} C:\VERSOES_FECHADAS\releaseversion"
        }) + "\r\n";
        var at = result.IndexOf(inserted);
        Assert.True(at >= 0, "bloco inserido nao encontrado no formato esperado");
        Assert.Equal(Sectioned, result.Remove(at, inserted.Length));
    }

    [Fact]
    public void Bkp_unknown_family_goes_before_concluido_and_therefore_before_the_cloud_copy()
    {
        var result = LibTemplateEditor.AddToBkp(Sectioned, new LibSpec("libOutraCoisa", @"%WORKSPACE%\x\", @"%WORKSPACE%\x\"));

        Assert.True(result.IndexOf("libOutraCoisa") < result.IndexOf("echo concluido"));
        Assert.True(result.IndexOf("echo concluido") < result.IndexOf("releaseversion_CLOUD"));
    }

    [Fact]
    public void Tintas_preset_uses_the_auttin_tintas_output()
    {
        var spec = LibSpec.Tintas("libTintaNova");
        Assert.Equal(@"%WORKSPACE%\padra_atualizado\auttin\tintas\libTintaNova.dll", spec.DllPath);
    }

    [Fact]
    public void Bkp_rejects_duplicates_and_missing_anchor()
    {
        Assert.Throws<LibEditException>(() => LibTemplateEditor.AddToBkp(Bkp, LibSpec.Cartoes("libCartoesSipag")));
        Assert.Throws<LibEditException>(() => LibTemplateEditor.AddToBkp("CALL x\r\n", LibSpec.Cartoes("libCartoesNovo")));
    }

    [Fact]
    public void Cmd_is_cloned_from_a_reference_changing_only_the_lib_name()
    {
        var result = LibTemplateEditor.CmdFromReference(Cmd, "libCartoesCielo", "libCartoesNovo");

        Assert.DoesNotContain("Cielo", result);
        Assert.Equal(Cmd.Replace("libCartoesCielo", "libCartoesNovo"), result);
        Assert.Contains("err_libCartoesNovo.dll", result);
        Assert.Contains("signtool", result);
    }

    [Fact]
    public void Jenkins_config_only_changes_the_command_with_concluido_and_escapes_xml()
    {
        var command = "echo a > b & type x <y>\r\nCALL C:\\Compilador\\ddcc32NovoDX10.cmd libCartoesSipag s o\r\n\r\necho concluido > C:\\x\\concluido.txt\r\n:END\r\n";
        var escaped = command.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#xd;");
        var config = "<?xml version='1.1' encoding='UTF-8'?>\n<project>\n  <builders>\n    <hudson.tasks.BatchFile>\n      <command>" + escaped +
                     "</command>\n    </hudson.tasks.BatchFile>\n  </builders>\n  <description>nao mexer</description>\n</project>";

        var result = LibTemplateEditor.AddToJobConfig(config, LibSpec.Cartoes("libCartoesNovo"));

        Assert.StartsWith("<?xml version='1.1' encoding='UTF-8'?>", result);   // declaracao 1.1 preservada (por isso sem XDocument)
        Assert.Contains("<description>nao mexer</description>", result);
        Assert.Contains("ddcc32NovoDX10.cmd libCartoesNovo", result);
        Assert.Contains("&amp;", result);                                      // o '&' original continua escapado
        Assert.Contains("&#xd;", result);                                      // CRLF segue no formato do Jenkins
        Assert.DoesNotContain("\r", result);                                   // nenhum CR cru dentro do XML
        Assert.True(LibTemplateEditor.JobConfigHas(result, "libCartoesNovo"));
    }

    [Fact]
    public void Jenkins_config_without_a_matching_command_is_rejected()
    {
        Assert.Throws<LibEditException>(() =>
            LibTemplateEditor.AddToJobConfig("<project><command>echo oi</command></project>", LibSpec.Cartoes("libCartoesNovo")));
    }

    // ------------------------------------------------------------------ remocao

    [Fact]
    public void Removing_what_was_added_restores_the_original_files_exactly()
    {
        Assert.Equal(Bat, LibTemplateEditor.RemoveFromBat(LibTemplateEditor.AddToBat(Bat, "libCartoesNovo"), "libCartoesNovo"));

        Assert.Equal(Bkp, LibTemplateEditor.RemoveFromBkp(
            LibTemplateEditor.AddToBkp(Bkp, LibSpec.Cartoes("libCartoesNovo")), "libCartoesNovo"));
        Assert.Equal(Sectioned, LibTemplateEditor.RemoveFromBkp(
            LibTemplateEditor.AddToBkp(Sectioned, LibSpec.Cartoes("libCartoesNovo")), "libCartoesNovo"));
        Assert.Equal(Sectioned, LibTemplateEditor.RemoveFromBkp(
            LibTemplateEditor.AddToBkp(Sectioned, LibSpec.Boletos("libBoletosNovo")), "libBoletosNovo"));
        var outra = new LibSpec("libOutraCoisa", @"%WORKSPACE%\x\", @"%WORKSPACE%\x\");
        Assert.Equal(Sectioned, LibTemplateEditor.RemoveFromBkp(LibTemplateEditor.AddToBkp(Sectioned, outra), "libOutraCoisa"));
    }

    [Fact]
    public void Removing_an_existing_lib_leaves_the_others_untouched()
    {
        var bat = LibTemplateEditor.RemoveFromBat(Bat, "libBoletosItau");
        Assert.DoesNotContain("libBoletosItau", bat);
        Assert.DoesNotContain("ExeclibBoletosItau", bat);
        Assert.Contains("SET ExeclibCartoesSipag=0\r\n", bat);
        Assert.Contains(@"comandosCMD\libCartoesSipag.cmd", bat);
        Assert.Contains("SET ExecAdvance=0", bat);
        Assert.Contains("echo Tempo decorrido", bat);

        var bkp = LibTemplateEditor.RemoveFromBkp(Sectioned, "libCartoesB");
        Assert.DoesNotContain("libCartoesB", bkp);
        Assert.Contains("libCartoesA", bkp);
        Assert.Contains("libBoletosA", bkp);
        Assert.Contains("echo concluido", bkp);
    }

    [Fact]
    public void Removal_of_a_lib_that_is_not_there_is_rejected()
    {
        Assert.Throws<LibEditException>(() => LibTemplateEditor.RemoveFromBat(Bat, "libCartoesNovo"));
        Assert.Throws<LibEditException>(() => LibTemplateEditor.RemoveFromBkp(Bkp, "libCartoesNovo"));
        Assert.Throws<LibEditException>(() => LibTemplateEditor.RemoveFromBat(Bat, "lib;rm"));
    }

    [Fact]
    public void Removal_refuses_blocks_outside_the_known_format_instead_of_guessing()
    {
        // bloco do bat sem o .cmd da lib
        var bat = Bat.Replace(@"comandosCMD\libBoletosItau.cmd", @"outraPasta\x.cmd");
        Assert.Throws<LibEditException>(() => LibTemplateEditor.RemoveFromBat(bat, "libBoletosItau"));

        // bkp: CALL sem as 3 linhas seguintes esperadas
        var bkp = "CALL C:\\Compilador\\ddcc32NovoDX10.cmd libX a b\r\necho outra coisa\r\nfoo\r\nbar\r\necho concluido\r\n";
        Assert.Throws<LibEditException>(() => LibTemplateEditor.RemoveFromBkp(bkp, "libX"));
    }

    [Fact]
    public void Jenkins_config_removal_undoes_the_addition()
    {
        var command = "echo a > b & type x <y>\r\nCALL C:\\Compilador\\ddcc32NovoDX10.cmd libCartoesSipag s o\r\nIF NOT %ERRORLEVEL% == 0 GOTO END\r\n" +
                      "C:\\Compilador\\madExceptPatch.exe olibCartoesSipag.dll\r\ncopy /Y olibCartoesSipag.dll C:\\x\r\n\r\necho concluido > C:\\x\\concluido.txt\r\n:END\r\n";
        string Xml(string c) => "<?xml version='1.1' encoding='UTF-8'?>\n<project><builders><command>" +
            c.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#xd;") + "</command></builders></project>";
        var config = Xml(command);

        var added = LibTemplateEditor.AddToJobConfig(config, LibSpec.Cartoes("libCartoesNovo"));
        Assert.True(LibTemplateEditor.JobConfigHas(added, "libCartoesNovo"));

        var restored = LibTemplateEditor.RemoveFromJobConfig(added, "libCartoesNovo");
        Assert.Equal(config, restored);
        Assert.False(LibTemplateEditor.JobConfigHas(restored, "libCartoesNovo"));
    }
}
