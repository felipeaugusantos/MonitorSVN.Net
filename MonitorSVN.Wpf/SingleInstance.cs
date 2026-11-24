using System.Security.Principal;
using System.Threading;

namespace MonitorSVN;

/// <summary>
/// Garante uma unica instancia do app por usuario do Windows (inclusive entre sessoes, ex.: area de trabalho remota).
/// A segunda instancia avisa a primeira, que se mostra, e encerra.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _signal;
    private RegisteredWaitHandle? _registration;

    public bool IsFirst { get; }

    public SingleInstance(string key)
    {
        var name = $@"Global\MonitorSVN.{key}";
        try
        {
            _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            IsFirst = createdNew;
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".show");
        }
        catch (UnauthorizedAccessException)
        {
            // o nome ja existe e nao temos acesso a ele: tratamos como "ja esta aberto"
            IsFirst = false;
        }
    }

    /// <summary>Chave do usuario atual: o SID, que nao muda com o nome de exibicao.</summary>
    public static string CurrentUserKey() =>
        WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;

    /// <summary>Primeira instancia: executa <paramref name="onSecondLaunch"/> (em outra thread) quando outra tentar abrir.</summary>
    public void Listen(Action onSecondLaunch)
    {
        if (_signal is null || !IsFirst) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal, (_, _) => onSecondLaunch(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Segunda instancia: pede para a primeira se mostrar.</summary>
    public void SignalFirst()
    {
        try { _signal?.Set(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _signal?.Dispose();
        if (_mutex is not null)
        {
            if (IsFirst)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }   // liberado por outra thread: o SO limpa ao fechar o processo
            }
            _mutex.Dispose();
        }
    }
}
