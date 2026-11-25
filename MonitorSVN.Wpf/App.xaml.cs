using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MonitorSVN.Services;

namespace MonitorSVN;

public partial class App : Application
{
    private SingleInstance? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new SingleInstance(SingleInstance.CurrentUserKey());
        if (!_single.IsFirst)
        {
            // ja existe uma instancia deste usuario: pede para ela aparecer e sai sem abrir janela
            AppLog.Info("Segunda instancia bloqueada; a primeira foi avisada.");
            _single.SignalFirst();
            _single.Dispose();
            _single = null;
            Shutdown();
            return;
        }

        AppLog.Purge();
        // logs de BAT de fechamento (um por fechamento) se acumulam: em segundo plano, sem atrasar a abertura
        _ = Task.Run(() =>
        {
            var removed = ClosingService.PurgeOldLogs(AppLog.Folder);
            if (removed > 0) AppLog.Info($"{removed} log(s) de BAT com mais de {ClosingService.BatLogRetentionDays} dias removido(s).");
        });
        AppLog.Info($"Iniciando Monitor SVN {Version()} (usuario {Environment.UserName}).");

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Excecao nao tratada na interface.", args.Exception);
            args.Handled = false;   // mantem o comportamento padrao (o log so registra)
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Excecao nao tratada (fatal).", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLog.Error("Excecao de tarefa nao observada.", args.Exception);

        base.OnStartup(e);

        // A janela e criada aqui (e nao por StartupUri): o WPF cria a janela do StartupUri mesmo depois de Shutdown(),
        // e uma segunda instancia nao pode montar a janela nem gravar configuracoes/historico.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _single.Listen(() => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromAnotherInstance()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_single is not null) AppLog.Info("Monitor SVN encerrado.");
        _single?.Dispose();
        base.OnExit(e);
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "?";
}
