using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MonitorSVN.Services;

/// <summary>
/// Acesso ao Jenkins pela API HTTP (usuario + senha, via HTTP Basic). Substitui o jenkins-cli.jar (Java)
/// e o disparo abrindo o navegador do app Delphi.
/// </summary>
public sealed class JenkinsClient : IDisposable
{
    private readonly HttpClient _http;

    public JenkinsClient(string baseUrl, string user, string password, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(baseUrl.Trim().TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        if (!string.IsNullOrEmpty(user))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
    }

    /// <summary>
    /// Endereco da pagina do job de uma versao (o job tem o mesmo nome da versao), para abrir no navegador.
    /// Retorna null se a URL do Jenkins nao for um http/https valido ou a versao estiver vazia.
    /// </summary>
    public static Uri? JobUrl(string jenkinsUrl, string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        if (!Uri.TryCreate(jenkinsUrl?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)) return null;
        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) return null;
        return new Uri(baseUri, $"job/{Uri.EscapeDataString(version.Trim())}/");
    }

    /// <summary>
    /// true quando as credenciais iriam por HTTP puro para outra maquina (qualquer um na rede le a senha).
    /// Loopback (localhost, 127.0.0.1, ::1) e HTTPS sao considerados seguros.
    /// </summary>
    public static bool SendsCredentialsInsecurely(string baseUrl, string user)
    {
        if (string.IsNullOrEmpty(user)) return false;
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;
    }

    public async Task<bool> JobExistsAsync(string name, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"job/{Uri.EscapeDataString(name)}/api/json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public Task<string> GetJobConfigAsync(string name, CancellationToken ct) =>
        _http.GetStringAsync($"job/{Uri.EscapeDataString(name)}/config.xml", ct);

    /// <summary>Substitui o config.xml de um job existente (usado para atualizar o job-modelo).</summary>
    public async Task UpdateJobConfigAsync(string name, string configXml, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"job/{Uri.EscapeDataString(name)}/config.xml")
        {
            Content = new StringContent(configXml, Encoding.UTF8, "application/xml")
        };
        await AddCrumbAsync(request, ct);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task CreateJobAsync(string name, string configXml, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"createItem?name={Uri.EscapeDataString(name)}")
        {
            Content = new StringContent(configXml, Encoding.UTF8, "application/xml")
        };
        await AddCrumbAsync(request, ct);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Dispara o build usando o modelo de URL configurado (ex.: .../job/%token%/build?token=%token%).</summary>
    public async Task TriggerBuildAsync(string urlTemplate, string version, CancellationToken ct)
    {
        var url = urlTemplate.Replace("%token%", Uri.EscapeDataString(version));
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("") };
        await AddCrumbAsync(request, ct);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Aborta o que o job da versao esta fazendo: tira o item da fila e manda parar os builds em execucao.
    /// Retorna true se havia algo para abortar (false tambem quando o job nao existe).
    /// </summary>
    public async Task<bool> AbortBuildAsync(string name, CancellationToken ct)
    {
        var job = $"job/{Uri.EscapeDataString(name)}";
        var aborted = false;

        using (var response = await _http.GetAsync($"{job}/api/json?tree=inQueue,queueItem[id]", ct))
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("inQueue", out var inQueue) && inQueue.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("queueItem", out var item) && item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("id", out var id))
            {
                await PostAsync($"queue/cancelItem?id={id.GetInt64()}", ct);
                aborted = true;
            }
        }

        var running = new List<int>();
        using (var response = await _http.GetAsync($"{job}/api/json?tree=builds[number,building]{{0,10}}", ct))
        {
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("builds", out var builds))
                foreach (var b in builds.EnumerateArray())
                    if (b.TryGetProperty("building", out var on) && on.ValueKind == JsonValueKind.True)
                        running.Add(b.GetProperty("number").GetInt32());
        }

        foreach (var number in running)
        {
            await PostAsync($"{job}/{number}/stop", ct);
            aborted = true;
        }
        return aborted;
    }

    private async Task PostAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("") };
        await AddCrumbAsync(request, ct);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Jenkins com protecao CSRF exige o "crumb". Se o servidor nao usa, segue sem ele.</summary>
    private async Task AddCrumbAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("crumbIssuer/api/json", ct);
            if (!response.IsSuccessStatusCode) return;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var field = doc.RootElement.GetProperty("crumbRequestField").GetString();
            var crumb = doc.RootElement.GetProperty("crumb").GetString();
            if (!string.IsNullOrEmpty(field) && !string.IsNullOrEmpty(crumb))
                request.Headers.TryAddWithoutValidation(field, crumb);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException)
        {
            // sem crumb: a requisicao segue e o servidor decide
        }
    }

    public void Dispose() => _http.Dispose();
}
