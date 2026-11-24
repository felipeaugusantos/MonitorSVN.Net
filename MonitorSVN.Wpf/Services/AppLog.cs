using System.Text;

namespace MonitorSVN.Services;

/// <summary>
/// Log em arquivo, um por dia (%APPDATA%\MonitorSVN\logs\monitorsvn-AAAAMMDD.log). Nunca lanca excecao:
/// falhar ao gravar o log nao pode derrubar o monitoramento. Nao registre senhas aqui.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _folder;

    public static string Folder
    {
        get
        {
            if (_folder is not null) return _folder;
            var root = Environment.GetEnvironmentVariable("MONITORSVN_DATA")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorSVN");
            return Path.Combine(root, "logs");
        }
    }

    /// <summary>Troca a pasta dos logs (testes). Passe null para voltar ao padrao.</summary>
    public static void Configure(string? folder) => _folder = folder;

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("AVISO", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERRO ", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            if (ex is not null) text += Environment.NewLine + ex;

            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, $"monitorsvn-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, text + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // log nunca pode causar falha
        }
    }

    /// <summary>Apaga logs mais antigos que <paramref name="keepDays"/> dias.</summary>
    public static void Purge(int keepDays = 30)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var limit = DateTime.Now.AddDays(-keepDays);
            foreach (var file in Directory.GetFiles(Folder, "monitorsvn-*.log"))
                if (File.GetLastWriteTime(file) < limit)
                    File.Delete(file);
        }
        catch
        {
            // idem
        }
    }
}
