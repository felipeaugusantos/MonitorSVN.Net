using System.Text.Json.Serialization;

namespace MonitorSVN.Models;

/// <summary>
/// Regra que escolhe o job-modelo do Jenkins e as versoes de arquivo conforme o nome da versao.
/// Substitui o bloco de if/else fixo de pCadastraTarefa do app Delphi.
/// </summary>
public sealed class VersionRule
{
    /// <summary>Trecho que o nome da versao deve conter. Vazio = regra padrao (casa com tudo).</summary>
    public string Pattern { get; set; } = "";
    public string TemplateJob { get; set; } = "";
    public string FileVersion { get; set; } = "";
    /// <summary>Se vazio, usa o nome da versao sem os 7 primeiros caracteres (ex.: 379.48.2.30.74.150 vira 2.30.74.150).</summary>
    public string? ProductVersion { get; set; }
}

public sealed class AppSettings
{
    public string SvnUrl { get; set; } = "svn://servidor.exemplo.com.br";
    public string SvnExecutable { get; set; } = "svn";
    public List<string> Directories { get; set; } = new();
    public List<string> IgnoredUsers { get; set; } = new();

    public string JenkinsUrl { get; set; } = "http://jenkins.exemplo/";
    public string BuildUrlTemplate { get; set; } = "http://jenkins.exemplo/job/%token%/build?token=%token%";
    public string JenkinsUser { get; set; } = "";
    public bool RememberCredentials { get; set; } = true;
    public string GenerationRoot { get; set; } = @"C:\MonitorSVN";

    /// <summary>"Light", "Dark" ou vazio (segue o tema do Windows).</summary>
    public string Theme { get; set; } = "";

    /// <summary>Minutos de build "Em andamento" a partir dos quais o app avisa que esta demorando.</summary>
    public int SlowAfterMinutes { get; set; } = 60;

    /// <summary>Inicio da ultima varredura em que todos os diretorios foram lidos. A proxima consulta o SVN a partir desse dia.</summary>
    public DateTime? LastScan { get; set; }

    /// <summary>Senha do Jenkins protegida pelo DPAPI do Windows (somente este usuario le).</summary>
    public string ProtectedPassword { get; set; } = "";

    public List<VersionRule> Rules { get; set; } = DefaultRules();

    /// <summary>Senha em memoria; nunca vai para o arquivo em texto puro.</summary>
    [JsonIgnore] public string Password { get; set; } = "";

    public static List<VersionRule> DefaultRules() => new()
    {
        new() { Pattern = "379.48.2.30.72",     TemplateJob = "_72_Modelo",     FileVersion = "48.2.30.72" },
        new() { Pattern = "379.48.2.30.74.74",  TemplateJob = "_74_74_Modelo",  FileVersion = "48.2.30.74", ProductVersion = "2.30.74.74" },
        new() { Pattern = "379.48.2.30.74.111", TemplateJob = "_74_111_Modelo", FileVersion = "48.2.30.74", ProductVersion = "2.30.74.111" },
        new() { Pattern = "379.48.2.30.74.150", TemplateJob = "_74_Modelo",     FileVersion = "48.2.30.74", ProductVersion = "2.30.74.150" },
        new() { Pattern = "",                   TemplateJob = "_74_Modelo",     FileVersion = "48.2.30.74" },
    };
}
