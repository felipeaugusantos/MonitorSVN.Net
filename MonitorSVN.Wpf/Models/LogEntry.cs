using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace MonitorSVN.Models;

/// <summary>Situacao de uma versao no fluxo de geracao (equivale ao STATUS N/I/G/A/E do app Delphi).</summary>
public enum BuildStatus
{
    Pending,     // N - aguardando disparo
    Ignored,     // I - substituida por um commit mais novo da mesma versao
    Generated,   // G - geracao concluida
    InProgress,  // A - build disparado no Jenkins
    Error        // E - geracao com erro
}

public sealed class LogEntry : INotifyPropertyChanged
{
    private BuildStatus _status;
    private string? _errorDetail;
    private bool _isChild;
    private bool _hasChildren;
    private bool _isExpanded;
    private int _childCount;

    public string Revision { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime Timestamp { get; set; }

    /// <summary>O aviso de erro / de build demorado desta linha já foi dado (não repete ao reabrir o app).</summary>
    public bool AlertedError { get; set; }
    public bool AlertedSlow { get; set; }
    public bool AlertedDone { get; set; }

    /// <summary>
    /// Data (UTC) de um erro.txt / concluido.txt velho que nao pode ser apagado (arquivo em uso) antes do disparo.
    /// Enquanto o arquivo tiver essa mesma data, ele e de um build anterior e nao conta como resultado deste.
    /// </summary>
    public DateTime? StaleErrorStamp { get; set; }
    public DateTime? StaleDoneStamp { get; set; }

    /// <summary>O fechamento automatico desta linha ja foi tentado (nao repete ao reabrir o app).</summary>
    public bool AutoClosed { get; set; }

    /// <summary>Quando o build foi disparado no Jenkins. Nulo se ainda não foi disparado.</summary>
    public DateTime? StartedAt { get; set; }

    private string _closingText = "";

    /// <summary>Situacao do fechamento da versao (coluna Fechamento). Nao e gravada: vem do ClosingService.</summary>
    [JsonIgnore]
    public string ClosingText
    {
        get => _closingText;
        set { if (_closingText == value) return; _closingText = value; OnChanged(); }
    }

    [JsonIgnore] public string StartedText => StartedAt is { } t ? t.ToString("HH:mm") : "—";

    /// <summary>Quando a geração terminou (Gerado ou Erro). Nulo enquanto não terminou.</summary>
    public DateTime? FinishedAt { get; set; }

    [JsonIgnore] public string FinishedText => FinishedAt is { } t ? t.ToString("HH:mm") : "—";

    /// <summary>Removido da lista pelo usuario. Fica guardado (oculto) para o commit nao ser recriado na proxima varredura.</summary>
    public bool Dismissed { get; set; }

    public BuildStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnChanged();
            OnChanged(nameof(StatusText));
            OnChanged(nameof(ToolTipText));
        }
    }

    private string? _suspect;

    /// <summary>Provável responsável pelo erro ("autor (Rev n)"). Nulo = ainda não pesquisado; vazio = pesquisado sem resultado.</summary>
    public string? Suspect
    {
        get => _suspect;
        set
        {
            if (_suspect == value) return;
            _suspect = value;
            OnChanged();
            OnChanged(nameof(ToolTipText));
        }
    }

    public string? ErrorDetail
    {
        get => _errorDetail;
        set
        {
            if (_errorDetail == value) return;
            _errorDetail = value;
            OnChanged();
            OnChanged(nameof(ToolTipText));
        }
    }

    /// <summary>true quando ha um commit mais antigo da mesma versao no mesmo dia (linha exibida recuada).</summary>
    [JsonIgnore]
    public bool IsChild
    {
        get => _isChild;
        set
        {
            if (_isChild == value) return;
            _isChild = value;
            OnChanged();
            OnChanged(nameof(RevisionText));
        }
    }

    /// <summary>Linha principal de um grupo com outros commits da mesma versao abaixo dela.</summary>
    [JsonIgnore]
    public bool HasChildren
    {
        get => _hasChildren;
        set { if (_hasChildren == value) return; _hasChildren = value; OnChanged(); }
    }

    [JsonIgnore]
    public int ChildCount
    {
        get => _childCount;
        set { if (_childCount == value) return; _childCount = value; OnChanged(); OnChanged(nameof(ChildCountText)); }
    }

    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnChanged(); OnChanged(nameof(ToggleGlyph)); }
    }

    // Segoe MDL2: ChevronDown (expandido) / ChevronRight (recolhido)
    [JsonIgnore] public string ToggleGlyph => IsExpanded ? "" : "";

    [JsonIgnore] public string ChildCountText => "+" + ChildCount;

    [JsonIgnore] public string RevisionText => (IsChild ? "↳ " : "") + "Rev: " + Revision;

    [JsonIgnore] public bool IsPast => Timestamp.Date < DateTime.Today;

    [JsonIgnore] public long RevisionNumber => long.TryParse(Revision, out var n) ? n : 0;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        BuildStatus.Pending => "Pendente",
        BuildStatus.Ignored => "Ignorado (existe commit mais novo desta versao)",
        BuildStatus.Generated => "Gerado",
        BuildStatus.InProgress => "Em andamento",
        BuildStatus.Error => "Erro",
        _ => ""
    };

    [JsonIgnore]
    public string ToolTipText
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(ErrorDetail) ? StatusText : StatusText + Environment.NewLine + ErrorDetail;
            if (Status == BuildStatus.Error && !string.IsNullOrWhiteSpace(Suspect))
                text += Environment.NewLine + Environment.NewLine + "Provável responsável: " + Suspect;
            return text;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
