using System.Security.Cryptography;
using System.Text.Json;

namespace MonitorSVN.Services;

public sealed record CopyProgress(string Phase, string File, long BytesDone, long BytesTotal);

public sealed class CopyResult
{
    public int CopiedFiles { get; init; }
    /// <summary>SHA-256 de cada ZIP copiado (nome do arquivo -> hash), para a auditoria.</summary>
    public Dictionary<string, string> Hashes { get; init; } = new();
    public int CleanedItems { get; init; }
    /// <summary>Texto do erro se a limpeza da origem falhou (a copia em si esta completa e verificada).</summary>
    public string? CleanupError { get; init; }
    /// <summary>Itens que ficaram na origem (nao eram ZIPs copiados).</summary>
    public List<string> RemainingItems { get; init; } = new();
    /// <summary>ZIPs que ja estavam no destino e nao fazem parte desta copia (nao sao apagados).</summary>
    public List<string> OnlyInDestination { get; init; } = new();
}

/// <summary>
/// Copia transacional dos ZIPs da versao para o destino de rede. Porta do _copy_project_files do VarreduraSistema:
/// copia para uma pasta temporaria no proprio destino, confere o SHA-256, guarda o que existia, promove os novos e
/// desfaz tudo se algo falhar. So depois remove da origem os ZIPs copiados que nao mudaram.
/// Diferencas: o hash da origem sai da mesma leitura da copia (cada arquivo e lido uma vez na origem e uma no
/// destino, antes eram quatro), ha cancelamento ate o inicio da promocao e pastas temporarias esquecidas por
/// uma queda sao removidas.
/// </summary>
public static class CopyService
{
    public const string TransactionPrefix = ".varredura-";
    private const int BufferSize = 1024 * 1024;
    private const int CleanupAttempts = 5;
    private static readonly TimeSpan OrphanAge = TimeSpan.FromDays(1);

    private static readonly string[] KeepInSource = { "comandoscmd", ProjectLock.FileName };

    public static Task<CopyResult> CopyAsync(string source, string destination, IProgress<CopyProgress>? progress,
        CancellationToken ct, TimeSpan? cleanupRetryDelay = null) =>
        Task.Run(() => Copy(source, destination, progress, ct, cleanupRetryDelay ?? TimeSpan.FromSeconds(1)), CancellationToken.None);

    private static CopyResult Copy(string source, string destination, IProgress<CopyProgress>? progress,
        CancellationToken ct, TimeSpan retryDelay)
    {
        var zipErrors = ZipNameValidator.Validate(source);
        if (zipErrors.Count > 0)
            throw new IOException("Copia bloqueada por ZIP invalido: " + string.Join("; ", zipErrors.Take(3)));

        var items = ZipsOf(source);
        if (items.Count == 0) throw new IOException("Nenhum arquivo .zip encontrado para copiar.");

        CleanOrphans(destination);

        var transaction = Path.Combine(destination, TransactionPrefix + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(transaction, "novos");
        var backup = Path.Combine(transaction, "anteriores");
        Directory.CreateDirectory(staging);

        var stamps = new Dictionary<string, (long Length, long Ticks)>();
        var hashes = new Dictionary<string, string>();
        var promoted = new List<string>();
        var backedUp = new List<string>();
        var removeTransaction = false;
        try
        {
            var total = items.Sum(p => new FileInfo(p).Length);
            long done = 0;
            foreach (var item in items)
            {
                var name = Path.GetFileName(item);
                var info = new FileInfo(item);
                stamps[name] = (info.Length, info.LastWriteTimeUtc.Ticks);
                hashes[name] = CopyFile(item, Path.Combine(staging, name), name, ref done, total, progress, ct);
                File.SetLastWriteTimeUtc(Path.Combine(staging, name), info.LastWriteTimeUtc);
            }

            // confere o que chegou ao destino (releitura) e se a origem nao mudou durante a copia
            long verified = 0;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(item);
                var now = new FileInfo(item);
                if (now.Length != stamps[name].Length || now.LastWriteTimeUtc.Ticks != stamps[name].Ticks)
                    throw new IOException($"Verificacao pos-copia: {name} foi alterado na origem durante a copia.");
                var staged = Path.Combine(staging, name);
                var stagedHash = HashFile(staged, name, "Verificando", ref verified, total, progress, ct);
                if (stagedHash != hashes[name])
                    throw new IOException($"Verificacao pos-copia encontrou arquivo divergente: {name}");
            }

            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(transaction, "manifest.json"), JsonSerializer.Serialize(new
            {
                source,
                destination,
                files = items.Select(Path.GetFileName).ToArray(),
                backup_directory = backup
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

            // ponto sem volta: a partir daqui nao ha mais cancelamento, so conclusao ou desfazer
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CopyProgress("Finalizando", "", total, total));
            foreach (var item in items)
            {
                var name = Path.GetFileName(item);
                var final = Path.Combine(destination, name);
                if (File.Exists(final))
                {
                    File.Move(final, Path.Combine(backup, name));
                    backedUp.Add(name);
                }
                File.Move(Path.Combine(staging, name), final);
                promoted.Add(name);
            }

            // a promocao e uma troca de nome no mesmo volume (o conteudo ja foi conferido): basta ver se chegou inteiro
            foreach (var name in promoted)
            {
                var final = new FileInfo(Path.Combine(destination, name));
                if (!final.Exists || final.Length != stamps[name].Length)
                    throw new IOException($"Verificacao no destino definitivo falhou: {name}");
            }
            removeTransaction = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            var rollbackErrors = Rollback(destination, backup, promoted, backedUp);
            if (rollbackErrors.Count > 0)
            {
                try { File.WriteAllText(Path.Combine(transaction, "ROLLBACK_INCOMPLETO.txt"), string.Join(Environment.NewLine, rollbackErrors)); }
                catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException) { }
                throw new IOException($"{ex.Message}. Rollback incompleto; backups preservados em {transaction}. " +
                                      $"Detalhes: {string.Join("; ", rollbackErrors.Take(3))}", ex);
            }
            removeTransaction = true;
            throw;
        }
        finally
        {
            if (removeTransaction) TryDeleteDirectory(transaction);
        }

        var (cleaned, cleanupError) = CleanSource(source, stamps, retryDelay);
        var copiedNames = new HashSet<string>(stamps.Keys, StringComparer.OrdinalIgnoreCase);
        return new CopyResult
        {
            CopiedFiles = items.Count,
            Hashes = hashes,
            CleanedItems = cleaned,
            CleanupError = cleanupError,
            RemainingItems = RemainingOf(source),
            OnlyInDestination = ZipsOf(destination).Select(p => Path.GetFileName(p)!).Where(n => !copiedNames.Contains(n)).ToList()
        };
    }

    private static List<string> ZipsOf(string folder) =>
        Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Copia o arquivo calculando o SHA-256 do que foi lido da origem.</summary>
    private static string CopyFile(string from, string to, string name, ref long done, long total,
        IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            done += read;
            progress?.Report(new CopyProgress("Copiando", name, done, total));
        }
        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string HashFile(string path, string name, string phase, ref long done, long total,
        IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(new CopyProgress(phase, name, done, total));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static List<string> Rollback(string destination, string backup, List<string> promoted, List<string> backedUp)
    {
        var errors = new List<string>();
        foreach (var name in Enumerable.Reverse(promoted))
        {
            var final = Path.Combine(destination, name);
            try { if (File.Exists(final)) File.Delete(final); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"remover {final}: {ex.Message}"); }
        }
        foreach (var name in Enumerable.Reverse(backedUp))
        {
            var saved = Path.Combine(backup, name);
            try { if (File.Exists(saved)) File.Move(saved, Path.Combine(destination, name), overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"restaurar {saved}: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>Remove so os ZIPs copiados que nao mudaram desde a copia. O resto fica e e listado ao operador.</summary>
    private static (int Cleaned, string? Error) CleanSource(string source, Dictionary<string, (long Length, long Ticks)> stamps, TimeSpan retryDelay)
    {
        var cleaned = 0;
        foreach (var (name, stamp) in stamps)
        {
            var path = Path.Combine(source, name);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != stamp.Length || info.LastWriteTimeUtc.Ticks != stamp.Ticks) continue;
            }
            catch (IOException) { continue; }

            try { DeleteWithRetry(path, retryDelay); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (cleaned, $"Erro ao limpar origem {path}: {ex.Message}");
            }
            cleaned++;
        }
        return (cleaned, null);
    }

    private static void DeleteWithRetry(string path, TimeSpan delay)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= CleanupAttempts) throw;
                Thread.Sleep(delay);
            }
        }
    }

    private static List<string> RemainingOf(string source)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(source)
                .Select(p => Path.GetFileName(p)!)
                .Where(n => !KeepInSource.Contains(n, StringComparer.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Apaga pastas temporarias de copias que cairam no meio (mais velhas que um dia e sem rollback pendente).</summary>
    public static int CleanOrphans(string destination, TimeSpan? olderThan = null)
    {
        var limit = DateTime.UtcNow - (olderThan ?? OrphanAge);
        var removed = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(destination, TransactionPrefix + "*"))
            {
                if (Directory.GetLastWriteTimeUtc(dir) > limit) continue;
                if (File.Exists(Path.Combine(dir, "ROLLBACK_INCOMPLETO.txt"))) continue;   // guarda backups: so o operador decide
                if (TryDeleteDirectory(dir)) removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Nao foi possivel limpar pastas temporarias antigas de {destination}.", ex);
        }
        if (removed > 0) AppLog.Info($"{removed} pasta(s) temporaria(s) de copia antiga removida(s) de {destination}.");
        return removed;
    }

    private static bool TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
