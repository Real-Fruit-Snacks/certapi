using System.ComponentModel;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>An open request tab: one editable <see cref="RequestModel"/> plus the response
/// it last produced. The title tracks the request's method and path.</summary>
public sealed class RequestTab : INotifyPropertyChanged
{
    public RequestModel Request { get; }

    private string _title = "New Request";
    public string Title { get => _title; private set { _title = value; Raise(nameof(Title)); } }

    // Transient response state for this tab (not persisted directly; Snapshot is).
    public ApiResponse? LastResponse { get; set; }
    public string LastRawText { get; set; } = "";
    public FormattedResponse? CachedPretty { get; set; }
    public string? CachedDiagnostics { get; set; }
    public HistorySnapshot? Snapshot { get; set; }

    /// <summary>Live network trace for this tab — requests sent and resources the Rendered view fetched.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NetworkEntry> Network { get; } = new();

    public RequestTab(RequestModel request)
    {
        Request = request;
        Request.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RequestModel.Method) or nameof(RequestModel.Path)) UpdateTitle();
        };
        UpdateTitle();
    }

    public void UpdateTitle()
    {
        var path = string.IsNullOrWhiteSpace(Request.Path) ? "/" : Request.Path.Trim();
        Title = $"{Request.Method}  {path}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}
