using MonitorSVN.Services;

namespace MonitorSVN.Tests;

public class JenkinsSecurityTests
{
    [Theory]
    [InlineData("http://localhost:8080/", "379.48.2.30.72.461", "http://localhost:8080/job/379.48.2.30.72.461/")]
    [InlineData("http://localhost:8080", "379.48.2.30.72.461", "http://localhost:8080/job/379.48.2.30.72.461/")]
    [InlineData("  https://jenkins.empresa.com.br/jenkins/  ", "379.48.2.30.74.150", "https://jenkins.empresa.com.br/jenkins/job/379.48.2.30.74.150/")]
    [InlineData("http://localhost:8080/", "versao com espaco", "http://localhost:8080/job/versao%20com%20espaco/")]
    [InlineData("http://localhost:8080/", "a/b?x=1#y", "http://localhost:8080/job/a%2Fb%3Fx%3D1%23y/")]   // nada escapa do caminho do job
    public void Job_url_points_to_the_job_page_of_the_version(string jenkins, string version, string expected) =>
        Assert.Equal(expected, JenkinsClient.JobUrl(jenkins, version)?.AbsoluteUri);

    [Theory]
    [InlineData("", "379.1")]
    [InlineData("isto nao e url", "379.1")]
    [InlineData("file:///C:/Windows/System32/calc.exe", "379.1")]   // so http/https chegam ao navegador
    [InlineData("javascript:alert(1)", "379.1")]
    [InlineData("http://localhost:8080/", "")]
    [InlineData("http://localhost:8080/", "   ")]
    public void Job_url_is_null_for_unsafe_or_incomplete_input(string jenkins, string version) =>
        Assert.Null(JenkinsClient.JobUrl(jenkins, version));

    [Theory]
    [InlineData("http://jenkins.empresa.com.br/", "admin", true)]
    [InlineData("http://192.168.0.10:8080/", "admin", true)]
    [InlineData("  http://jenkins.empresa.com.br  ", "admin", true)]
    [InlineData("https://jenkins.empresa.com.br/", "admin", false)]
    [InlineData("http://localhost:8080/", "admin", false)]
    [InlineData("http://127.0.0.1:8080/", "admin", false)]
    [InlineData("http://[::1]:8080/", "admin", false)]
    [InlineData("http://jenkins.empresa.com.br/", "", false)]   // sem usuario nao ha credencial a expor
    [InlineData("isto nao e uma url", "admin", false)]
    public void Warns_only_when_credentials_go_over_plain_http_to_another_machine(string url, string user, bool expected) =>
        Assert.Equal(expected, JenkinsClient.SendsCredentialsInsecurely(url, user));
}
