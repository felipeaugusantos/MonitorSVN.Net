using System.Text.Json;
using MonitorSVN.Models;

namespace MonitorSVN.Services;

/// <summary>
/// Avisa a Varredura (outro sistema, so leitura) que uma versao terminou de ser gerada.
/// Grava uma linha JSON por evento em "&lt;pasta raiz&gt;\_monitorsvn_eventos.jsonl".
/// O arquivo fica na RAIZ de proposito: a Varredura so trata subpastas como projetos,
/// entao ele nao interfere na varredura nem entra na pasta da versao.
/// Falha ao gravar nunca atrapalha o monitoramento.
/// </summary>
public static class VarreduraNotifier
{
    public const string FileName = "_monitorsvn_eventos.jsonl";

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly object Gate = new();

    public static void Notify(AppSettings settings, LogEntry entry)
    {
        if (entry.Status is not (BuildStatus.Generated or BuildStatus.Error)) return;

        var evento = new
        {
            quando = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            versao = entry.Version,
            revisao = entry.Revision,
            autor = entry.Author,
            commit_em = entry.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss"),
            status = entry.Status == BuildStatus.Generated ? "gerado" : "erro",
            detalhe = entry.Status == BuildStatus.Error ? entry.ErrorDetail : null
        };

        try
        {
            Directory.CreateDirectory(settings.GenerationRoot);
            var path = Path.Combine(settings.GenerationRoot, FileName);
            var line = JsonSerializer.Serialize(evento, Json) + Environment.NewLine;
            lock (Gate)
            {
                // FileShare.ReadWrite: a Varredura pode estar lendo ao mesmo tempo
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // pasta de rede indisponivel etc.: o aviso e opcional
        }
    }
}
