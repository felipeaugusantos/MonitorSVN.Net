using System.Text;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN.Tests;

[Collection("AppLog")]   // ApplyAsync grava no AppLog (estatico): isola do log real e de testes paralelos
public class TemplateLibServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "msvn-tpl-" + Guid.NewGuid().ToString("N"));
    private readonly Encoding _enc = Encoding.Latin1;

    public TemplateLibServiceTests() => AppLog.Configure(Path.Combine(_root, "logs"));

    public void Dispose()
    {
        AppLog.Configure(null);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Bat = "SET ExeclibCartoesSipag=0\r\n\r\n:START\r\n\r\necho Tempo decorrido: %X%\r\n";
    private const string Bkp = "CALL C:\\Compilador\\ddcc32NovoDX10.cmd libCartoesSipag a b\r\n\r\necho concluido > C:\\x\r\n:END\r\n";
    private const string Cmd = "ResourceHacker libCartoesSipag.dll\r\nsigntool libCartoesSipag.dll\r\n";

    private AppSettings MakeTemplate(string name, bool withBkp = true)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "comandosCMD"));
        File.WriteAllText(Path.Combine(folder, "_FechamentoArquivos.bat"), Bat, _enc);
        if (withBkp) File.WriteAllText(Path.Combine(folder, "_ComandosBKP.txt"), Bkp, _enc);
        File.WriteAllText(Path.Combine(folder, "comandosCMD", "libCartoesSipag.cmd"), Cmd, _enc);
        return new AppSettings { GenerationRoot = _root };
    }

    [Fact]
    public async Task Plan_then_apply_updates_files_creates_cmd_and_backs_up_originals()
    {
        var settings = MakeTemplate("_74_Modelo");
        var lib = LibSpec.Cartoes("libCartoesNovo");

        var plan = await TemplateLibService.PlanAsync("_74_Modelo", lib, settings, null, CancellationToken.None);
        Assert.True(plan.Ok, plan.Error);

        // planejar nao grava nada
        Assert.Equal(Bat, File.ReadAllText(plan.BatPath!, _enc));

        var backup = await TemplateLibService.ApplyAsync(new[] { plan }, lib.Name, null, Path.Combine(_root, "bkp"), CancellationToken.None);

        var folder = Path.Combine(_root, "_74_Modelo");
        Assert.Contains("ExeclibCartoesNovo", File.ReadAllText(Path.Combine(folder, "_FechamentoArquivos.bat"), _enc));
        Assert.Contains("libCartoesNovo", File.ReadAllText(Path.Combine(folder, "_ComandosBKP.txt"), _enc));
        Assert.Equal(Cmd.Replace("libCartoesSipag", "libCartoesNovo"),
            File.ReadAllText(Path.Combine(folder, "comandosCMD", "libCartoesNovo.cmd"), _enc));

        // originais guardados
        Assert.Equal(Bat, File.ReadAllText(Path.Combine(backup, "_74_Modelo", "_FechamentoArquivos.bat"), _enc));
        Assert.Equal(Bkp, File.ReadAllText(Path.Combine(backup, "_74_Modelo", "_ComandosBKP.txt"), _enc));
    }

    [Fact]
    public async Task Add_then_remove_restores_the_template_and_backs_up_the_deleted_cmd()
    {
        var settings = MakeTemplate("_74_Modelo");
        var lib = LibSpec.Cartoes("libCartoesNovo");
        var folder = Path.Combine(_root, "_74_Modelo");
        var add = await TemplateLibService.PlanAsync("_74_Modelo", lib, settings, null, CancellationToken.None);
        await TemplateLibService.ApplyAsync(new[] { add }, lib.Name, null, Path.Combine(_root, "bkp"), CancellationToken.None);

        var plan = await TemplateLibService.PlanRemoveAsync("_74_Modelo", lib.Name, settings, null, CancellationToken.None);
        Assert.True(plan.Ok, plan.Error);
        Assert.Contains("libCartoesNovo.cmd", plan.Summary);
        Assert.True(File.Exists(Path.Combine(folder, "comandosCMD", "libCartoesNovo.cmd")));   // planejar nao apaga nada

        var backup = await TemplateLibService.ApplyAsync(new[] { plan }, lib.Name, null, Path.Combine(_root, "bkp"), CancellationToken.None);

        Assert.Equal(Bat, File.ReadAllText(Path.Combine(folder, "_FechamentoArquivos.bat"), _enc));
        Assert.Equal(Bkp, File.ReadAllText(Path.Combine(folder, "_ComandosBKP.txt"), _enc));
        Assert.False(File.Exists(Path.Combine(folder, "comandosCMD", "libCartoesNovo.cmd")));
        Assert.True(File.Exists(Path.Combine(folder, "comandosCMD", "libCartoesSipag.cmd")));   // as outras ficam
        Assert.True(File.Exists(Path.Combine(backup, "_74_Modelo", "libCartoesNovo.cmd")));
        Assert.EndsWith("-remover-libCartoesNovo", backup);
    }

    [Fact]
    public async Task Removing_a_lib_the_template_does_not_have_is_reported()
    {
        var settings = MakeTemplate("_74_Modelo");

        var plan = await TemplateLibService.PlanRemoveAsync("_74_Modelo", "libCartoesNovo", settings, null, CancellationToken.None);

        Assert.True(plan.NotFound);
        Assert.False(plan.Ok);
    }

    [Fact]
    public async Task Existing_lib_is_reported_and_nothing_is_planned()
    {
        var settings = MakeTemplate("_74_Modelo");

        var plan = await TemplateLibService.PlanAsync("_74_Modelo", LibSpec.Cartoes("libCartoesSipag"), settings, null, CancellationToken.None);

        Assert.True(plan.AlreadyExists);
        Assert.False(plan.Ok);
    }

    [Fact]
    public async Task Missing_bkp_file_yields_an_error_plan_and_a_bad_template_blocks_nothing_else()
    {
        var settings = MakeTemplate("_74_Modelo");
        MakeTemplate("_72_Modelo", withBkp: false);
        var lib = LibSpec.Cartoes("libCartoesNovo");

        var good = await TemplateLibService.PlanAsync("_74_Modelo", lib, settings, null, CancellationToken.None);
        var bad = await TemplateLibService.PlanAsync("_72_Modelo", lib, settings, null, CancellationToken.None);

        Assert.True(good.Ok);
        Assert.False(bad.Ok);
        Assert.Contains("_ComandosBKP.txt", bad.Error);

        // aplicar so grava o que esta ok
        await TemplateLibService.ApplyAsync(new[] { good, bad }, lib.Name, null, Path.Combine(_root, "bkp"), CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_root, "_74_Modelo", "comandosCMD", "libCartoesNovo.cmd")));
        Assert.False(File.Exists(Path.Combine(_root, "_72_Modelo", "comandosCMD", "libCartoesNovo.cmd")));
    }

    [Fact]
    public void Lists_the_libs_known_by_a_template()
    {
        var settings = MakeTemplate("_74_Modelo");
        File.WriteAllText(Path.Combine(_root, "_74_Modelo", "_FechamentoArquivos.bat"),
            "start /MIN C:\\V\\%versao%\\comandosCMD\\libCartoesSipag.cmd\r\n", _enc);

        Assert.Equal(new[] { "libCartoesSipag" }, TemplateLibService.ListLibs(settings, "_74_Modelo"));
    }
}
