using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>Configura os avisos por e-mail (SMTP) e no Google Chat (webhook), cada um com envio de teste.</summary>
public sealed class EmailWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CheckBox _enabled = new() { Content = "Enviar e-mail quando uma versão terminar com erro ou um build demorar" };
    private readonly TextBox _host = new();
    private readonly TextBox _port = new() { Width = 80, MaxLength = 5 };
    private readonly CheckBox _ssl = new() { Content = "Usar SSL/TLS", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextBox _user = new();
    private readonly PasswordBox _password = new();
    private readonly TextBox _from = new();
    private readonly TextBox _to = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _test = new() { Content = "Testar e-mail", Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
    private readonly CheckBox _chatEnabled = new() { Content = "Enviar também para um espaço do Google Chat" };
    private readonly PasswordBox _webhook = new();
    private readonly Button _testChat = new() { Content = "Testar Chat", Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };

    public EmailWindow(Window owner, AppSettings settings)
    {
        _settings = settings;
        Owner = owner;
        Title = "Avisos";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        _chatEnabled.IsChecked = settings.ChatEnabled;
        _webhook.Password = settings.ChatWebhook;
        _enabled.IsChecked = settings.EmailEnabled;
        _host.Text = settings.SmtpHost;
        _port.Text = settings.SmtpPort.ToString();
        _ssl.IsChecked = settings.SmtpSsl;
        _user.Text = settings.SmtpUser;
        _password.Password = settings.SmtpPassword;
        _from.Text = settings.EmailFrom;
        _to.Text = settings.EmailTo;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(_enabled);

        panel.Children.Add(Label("Servidor SMTP e porta"));
        var server = new Grid();
        server.ColumnDefinitions.Add(new ColumnDefinition());
        server.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        server.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _host.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_port, 1);
        Grid.SetColumn(_ssl, 2);
        server.Children.Add(_host);
        server.Children.Add(_port);
        server.Children.Add(_ssl);
        panel.Children.Add(server);

        panel.Children.Add(Label("Usuário (deixe vazio se o servidor não exige login)"));
        panel.Children.Add(_user);
        panel.Children.Add(Label("Senha (guardada protegida, só este usuário do Windows lê)"));
        panel.Children.Add(_password);
        panel.Children.Add(Label("Remetente"));
        panel.Children.Add(_from);
        panel.Children.Add(Label("Destinatários (separe por ponto e vírgula)"));
        panel.Children.Add(_to);

        var chatTitle = new TextBlock { Text = "GOOGLE CHAT", FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 0) };
        chatTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(chatTitle);
        _chatEnabled.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_chatEnabled);
        panel.Children.Add(Label("Endereço do webhook (no Chat: espaço > Apps e integrações > Webhooks)"));
        panel.Children.Add(_webhook);

        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
            Text = "Cada versão gera um aviso só, igual ao balão da bandeja. Se o envio falhar, o app mostra o motivo na faixa " +
                   "de aviso e não tenta de novo aquele aviso. Gmail e Office 365 pedem senha de aplicativo, não a senha normal. " +
                   "O endereço do webhook funciona como senha do espaço: não o compartilhe."
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(note);
        panel.Children.Add(_status);

        var ok = new Button { Content = "Salvar", Width = 90, Height = 34, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "Cancelar", Width = 90, Height = 34, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_test);
        buttons.Children.Add(_testChat);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _test.Click += async (_, _) => await TestAsync();
        _testChat.Click += async (_, _) => await TestChatAsync();
        ok.Click += (_, _) => Save();
    }

    private static TextBlock Label(string text)
    {
        var l = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        return l;
    }

    private AppSettings Draft()
    {
        var port = int.TryParse(_port.Text.Trim(), out var p) ? p : 0;
        return new AppSettings
        {
            EmailEnabled = _enabled.IsChecked == true, SmtpHost = _host.Text.Trim(), SmtpPort = port,
            SmtpSsl = _ssl.IsChecked == true, SmtpUser = _user.Text.Trim(), SmtpPassword = _password.Password,
            EmailFrom = _from.Text.Trim(), EmailTo = _to.Text.Trim(),
            ChatEnabled = _chatEnabled.IsChecked == true, ChatWebhook = _webhook.Password.Trim()
        };
    }

    private void Show(string text, bool error)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "SuccessBrush");
    }

    private async Task TestAsync()
    {
        var d = Draft();
        if (EmailNotifier.Validate(d) is { } problem) { Show(problem, true); return; }
        _test.IsEnabled = false;
        Show("Enviando…", false);
        try
        {
            await EmailNotifier.SendAsync(d, "Monitor SVN: e-mail de teste",
                "Se você recebeu esta mensagem, o aviso por e-mail do Monitor SVN está configurado.");
            Show("Teste enviado para " + string.Join(", ", EmailNotifier.ParseRecipients(d.EmailTo)) + ".", false);
        }
        catch (Exception ex) { Show("Falhou: " + (ex.InnerException?.Message ?? ex.Message), true); }
        finally { _test.IsEnabled = true; }
    }

    private async Task TestChatAsync()
    {
        var d = Draft();
        if (ChatNotifier.Validate(d) is { } problem) { Show(problem, true); return; }
        _testChat.IsEnabled = false;
        Show("Enviando…", false);
        try
        {
            await ChatNotifier.SendAsync(d, "Monitor SVN: mensagem de teste. O aviso no Google Chat está configurado.");
            Show("Teste enviado ao espaço do Chat. Confira se a mensagem apareceu lá.", false);
        }
        catch (Exception ex) { Show("Falhou: " + (ex.InnerException?.Message ?? ex.Message), true); }
        finally { _testChat.IsEnabled = true; }
    }

    private void Save()
    {
        var d = Draft();
        // desligado, aceita campos incompletos; ligado, so salva configuracao que consegue enviar
        if (d.EmailEnabled && EmailNotifier.Validate(d) is { } problem) { Show(problem, true); return; }
        if (d.ChatEnabled && ChatNotifier.Validate(d) is { } chatProblem) { Show(chatProblem, true); return; }

        _settings.EmailEnabled = d.EmailEnabled;
        _settings.SmtpHost = d.SmtpHost;
        _settings.SmtpPort = d.SmtpPort is >= 1 and <= 65535 ? d.SmtpPort : 587;
        _settings.SmtpSsl = d.SmtpSsl;
        _settings.SmtpUser = d.SmtpUser;
        _settings.SmtpPassword = d.SmtpPassword;
        _settings.EmailFrom = d.EmailFrom;
        _settings.EmailTo = d.EmailTo;
        _settings.ChatEnabled = d.ChatEnabled;
        _settings.ChatWebhook = d.ChatWebhook;
        DialogResult = true;
    }
}
