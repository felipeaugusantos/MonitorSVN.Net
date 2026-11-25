using System.Windows;
using System.Windows.Controls;
using MonitorSVN.Models;
using MonitorSVN.Services;

namespace MonitorSVN;

/// <summary>Configura os avisos por e-mail (SMTP) e no Google Chat (webhook): destino, modelo da mensagem e envio de teste.</summary>
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
    private readonly TextBox _emailTemplate = NewTemplateBox();
    private readonly TextBlock _emailPreview = new();
    private readonly CheckBox _chatEnabled = new() { Content = "Enviar também para um espaço do Google Chat" };
    private readonly PasswordBox _webhook = new();
    private readonly TextBox _chatTemplate = NewTemplateBox();
    private readonly TextBlock _chatPreview = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _test = new() { Content = "Testar e-mail", Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _testChat = new() { Content = "Testar Chat", Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };

    private static TextBox NewTemplateBox() => new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 50, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6)
    };

    public EmailWindow(Window owner, AppSettings settings)
    {
        _settings = settings;
        Owner = owner;
        Title = "Avisos";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        ThemeManager.Attach(this);

        _enabled.IsChecked = settings.EmailEnabled;
        _host.Text = settings.SmtpHost;
        _port.Text = settings.SmtpPort.ToString();
        _ssl.IsChecked = settings.SmtpSsl;
        _user.Text = settings.SmtpUser;
        _password.Password = settings.SmtpPassword;
        _from.Text = settings.EmailFrom;
        _to.Text = settings.EmailTo;
        _emailTemplate.Text = AlertTemplate.Or(settings.EmailLineTemplate);
        _chatEnabled.IsChecked = settings.ChatEnabled;
        _webhook.Password = settings.ChatWebhook;
        _chatTemplate.Text = AlertTemplate.Or(settings.ChatLineTemplate);

        // ---- aba E-mail
        var mail = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        mail.Children.Add(_enabled);
        mail.Children.Add(Label("Servidor SMTP e porta"));
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
        mail.Children.Add(server);
        mail.Children.Add(Label("Usuário (deixe vazio se o servidor não exige login)"));
        mail.Children.Add(_user);
        mail.Children.Add(Label("Senha (guardada protegida, só este usuário do Windows lê)"));
        mail.Children.Add(_password);
        mail.Children.Add(Label("Remetente"));
        mail.Children.Add(_from);
        mail.Children.Add(Label("Destinatários (separe por ponto e vírgula)"));
        mail.Children.Add(_to);
        AddTemplate(mail, _emailTemplate, _emailPreview);

        // ---- aba Google Chat
        var chat = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        chat.Children.Add(_chatEnabled);
        chat.Children.Add(Label("Endereço do webhook (no Chat: espaço > Apps e integrações > Webhooks)"));
        chat.Children.Add(_webhook);
        AddTemplate(chat, _chatTemplate, _chatPreview);
        var hook = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 0),
            Text = "O endereço do webhook funciona como senha do espaço: não o compartilhe. No Chat, *texto* fica em negrito."
        };
        hook.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        chat.Children.Add(hook);

        // abas feitas com botoes: o TabControl padrao do WPF ignora o tema escuro
        var tabMail = new Button { Content = "E-mail", Height = 32, Padding = new Thickness(20, 0, 20, 0), Margin = new Thickness(0, 0, 8, 0) };
        var tabChat = new Button { Content = "Google Chat", Height = 32, Padding = new Thickness(20, 0, 20, 0) };
        void Select(bool showMail)
        {
            mail.Visibility = showMail ? Visibility.Visible : Visibility.Collapsed;
            chat.Visibility = showMail ? Visibility.Collapsed : Visibility.Visible;
            if (showMail) tabMail.SetResourceReference(StyleProperty, "PrimaryButton"); else tabMail.ClearValue(StyleProperty);
            if (showMail) tabChat.ClearValue(StyleProperty); else tabChat.SetResourceReference(StyleProperty, "PrimaryButton");
        }
        tabMail.Click += (_, _) => Select(true);
        tabChat.Click += (_, _) => Select(false);
        Select(true);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        tabs.Children.Add(tabMail);
        tabs.Children.Add(tabChat);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(tabs);
        panel.Children.Add(mail);
        panel.Children.Add(chat);
        var note = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
            Text = "Cada versão gera um aviso só, igual ao balão da bandeja. Se o envio falhar, o app mostra o motivo na faixa " +
                   "de aviso e não tenta de novo aquele aviso. Gmail e Office 365 pedem senha de aplicativo, não a senha normal."
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

        _emailTemplate.TextChanged += (_, _) => RefreshPreviews();
        _chatTemplate.TextChanged += (_, _) => RefreshPreviews();
        RefreshPreviews();

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

    /// <summary>Caixa do modelo da mensagem, ajuda dos campos, exemplo ja preenchido e botao de voltar ao padrao.</summary>
    private void AddTemplate(StackPanel parent, TextBox box, TextBlock preview)
    {
        parent.Children.Add(Label("Mensagem de cada aviso (uma linha por versão)"));
        parent.Children.Add(box);

        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0),
            Text = "Campos: " + string.Join("  ", AlertTemplate.Fields.Select(f => "{" + f + "}")) +
                   ".  Trecho entre colchetes, como [ Responsável: {responsavel}], só aparece se o campo tiver valor."
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        parent.Children.Add(help);

        var previewRow = new DockPanel { LastChildFill = true };
        var reset = new Button { Content = "Voltar ao padrão", Height = 26, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 6, 0, 0) };
        reset.Click += (_, _) => box.Text = AlertTemplate.DefaultLine;
        DockPanel.SetDock(reset, Dock.Right);
        previewRow.Children.Add(reset);
        previewRow.Children.Add(Label("Como vai ficar (exemplo)"));
        parent.Children.Add(previewRow);
        preview.TextWrapping = TextWrapping.Wrap;
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 8, 10, 8), Child = preview };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        parent.Children.Add(frame);

    }

    private void RefreshPreviews()
    {
        var d = Draft();
        Preview(_emailTemplate, _emailPreview, d);
        Preview(_chatTemplate, _chatPreview, d);
    }

    private void Preview(TextBox box, TextBlock target, AppSettings d)
    {
        if (AlertTemplate.UnknownField(box.Text) is { } unknown)
        {
            target.Text = $"Campo desconhecido: {{{unknown}}}";
            target.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }
        target.Text = "• " + AlertTemplate.Preview(box.Text, d);
        target.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
    }

    private AppSettings Draft()
    {
        var port = int.TryParse(_port.Text.Trim(), out var p) ? p : 0;
        return new AppSettings
        {
            EmailEnabled = _enabled.IsChecked == true, SmtpHost = _host.Text.Trim(), SmtpPort = port,
            SmtpSsl = _ssl.IsChecked == true, SmtpUser = _user.Text.Trim(), SmtpPassword = _password.Password,
            EmailFrom = _from.Text.Trim(), EmailTo = _to.Text.Trim(), EmailLineTemplate = _emailTemplate.Text.Trim(),
            ChatEnabled = _chatEnabled.IsChecked == true, ChatWebhook = _webhook.Password.Trim(),
            ChatLineTemplate = _chatTemplate.Text.Trim(),
            JenkinsUrl = _settings.JenkinsUrl
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
        if (AlertTemplate.UnknownField(d.EmailLineTemplate) is { } unknown) { Show($"Campo desconhecido no modelo: {{{unknown}}}", true); return; }
        _test.IsEnabled = false;
        Show("Enviando…", false);
        try
        {
            await EmailNotifier.SendAsync(d, "Monitor SVN: e-mail de teste",
                "Mensagem de teste. Assim vai aparecer cada aviso:" + Environment.NewLine + Environment.NewLine +
                "• " + AlertTemplate.Preview(d.EmailLineTemplate, d));
            Show("Teste enviado para " + string.Join(", ", EmailNotifier.ParseRecipients(d.EmailTo)) + ".", false);
        }
        catch (Exception ex) { Show("Falhou: " + (ex.InnerException?.Message ?? ex.Message), true); }
        finally { _test.IsEnabled = true; }
    }

    private async Task TestChatAsync()
    {
        var d = Draft();
        if (ChatNotifier.Validate(d) is { } problem) { Show(problem, true); return; }
        if (AlertTemplate.UnknownField(d.ChatLineTemplate) is { } unknown) { Show($"Campo desconhecido no modelo: {{{unknown}}}", true); return; }
        _testChat.IsEnabled = false;
        Show("Enviando…", false);
        try
        {
            await ChatNotifier.SendAsync(d, "*Monitor SVN: mensagem de teste*" + Environment.NewLine +
                "• " + AlertTemplate.Preview(d.ChatLineTemplate, d));
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
        foreach (var t in new[] { d.EmailLineTemplate, d.ChatLineTemplate })
            if (AlertTemplate.UnknownField(t) is { } unknown) { Show($"Campo desconhecido no modelo: {{{unknown}}}", true); return; }

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
        // igual ao padrao = guarda vazio, para acompanhar melhorias do padrao no futuro
        _settings.EmailLineTemplate = d.EmailLineTemplate == AlertTemplate.DefaultLine ? "" : d.EmailLineTemplate;
        _settings.ChatLineTemplate = d.ChatLineTemplate == AlertTemplate.DefaultLine ? "" : d.ChatLineTemplate;
        DialogResult = true;
    }
}
