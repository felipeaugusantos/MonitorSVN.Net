namespace MonitorSVN.Services;

public sealed class SvnClient
{
    private readonly string _exe;
    private readonly TimeSpan _timeout;

    public SvnClient(string executable = "svn", TimeSpan? timeout = null)
    {
        _exe = string.IsNullOrWhiteSpace(executable) ? "svn" : executable;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>svn log -r N -v --xml: arquivos alterados por uma revisão.</summary>
    public async Task<string> RevisionPathsAsync(string baseUrl, string revision, CancellationToken ct)
    {
        var args = new[] { "log", "-r", revision, "-v", "--xml", "--non-interactive", baseUrl.Trim().TrimEnd('/') };
        var (code, output, error) = await ProcessRunner.RunAsync(_exe, args, _timeout, ct);
        if (code != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"svn terminou com codigo {code}." : error.Trim());
        return output;
    }

    /// <summary>svn log -r {AAAA-MM-DD}:HEAD -v -q --xml --non-interactive URL</summary>
    public async Task<string> LogAsync(string baseUrl, string dir, DateOnly day, CancellationToken ct)
    {
        var url = baseUrl.Trim().TrimEnd('/') + dir;
        var args = new[]
        {
            "log", "-r", "{" + day.ToString("yyyy-MM-dd") + "}:HEAD",
            "-v", "-q", "--xml", "--non-interactive", url
        };

        var (code, output, error) = await ProcessRunner.RunAsync(_exe, args, _timeout, ct);
        if (code != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"svn terminou com codigo {code}." : error.Trim());
        return output;
    }
}
