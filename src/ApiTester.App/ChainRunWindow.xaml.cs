using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Runs one saved chain, here in the app, through the exact same Core engine
/// <c>certapi run --chain</c> uses (<see cref="ChainRunner"/> driving <see cref="RequestRunner"/>) —
/// there is one execution path, not an app copy of one. Each step's result is shown as it completes,
/// and the step actually selected shows its real response, so a failure at step 3 is inspectable
/// instead of just a verdict.</summary>
public partial class ChainRunWindow : Window
{
    private readonly AppState _state;
    private readonly ApiClient _client;
    private readonly TrustPredicates _predicates;
    private readonly RequestChain _chain;
    private readonly Func<string, X509Certificate2?> _findCertificate;

    private readonly ObservableCollection<StepRow> _rows = new();

    // Ctx.Note fires on whatever thread RequestRunner happens to continue on (its own awaits use
    // ConfigureAwait(false)), so notes are only ever accumulated here under a lock; they are drained
    // on the UI thread inside the progress handler, never touched directly from Note itself.
    private readonly object _noteLock = new();
    private readonly List<string> _pendingNotes = new();

    private CancellationTokenSource? _cts;

    /// <summary>A one-line summary for the owner's status bar, set when a run finishes. Null until
    /// one has.</summary>
    public string? Summary { get; private set; }

    public ChainRunWindow(AppState state, ApiClient client, TrustPredicates predicates,
                          RequestChain chain, Func<string, X509Certificate2?> findCertificate)
    {
        InitializeComponent();
        _state = state;
        _client = client;
        _predicates = predicates;
        _chain = chain;
        _findCertificate = findCertificate;

        HeaderText.Text = string.IsNullOrWhiteSpace(chain.Name) ? "RUN CHAIN" : chain.Name.ToUpperInvariant();
        EnvironmentText.Text = string.IsNullOrWhiteSpace(chain.EnvironmentName) ? "(no environment)" : chain.EnvironmentName;

        for (int i = 0; i < chain.Steps.Count; i++)
        {
            var step = chain.Steps[i];
            var node = FindRequest(state.Collections, step.RequestId);
            string label = node?.Name
                ?? (string.IsNullOrEmpty(step.RequestId) ? "(missing request)" : $"(missing request: {step.RequestId})");
            _rows.Add(StepRow.Pending(i + 1, label));
        }
        ResultsGrid.ItemsSource = _rows;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Stop any in-flight chain run so closing this window doesn't leave requests going out
        // after it's gone.
        _cts?.Cancel();
        base.OnClosing(e);
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ResolvedChainStep> steps;
        try { steps = ChainRunner.Resolve(_state, _chain); }
        catch (ChainRunException ex) { StatusText.Text = ex.Message; return; }

        for (int i = 0; i < _rows.Count; i++) _rows[i] = StepRow.Pending(i + 1, _rows[i].Label);

        string? envName = ChainRunner.PrepareCaptureEnvironment(_state, _chain);
        var vars = new RunVariables(() => VariablesFor(_state, envName));

        var ctx = new RequestRunContext
        {
            State = _state,
            Client = _client,
            Predicates = _predicates,
            FindCertificate = _findCertificate,
            Record = true,          // the live session owns state.json; markers persist when the app closes
            NoAutoToken = false,
            StrictVars = false,
            ShowRedirects = false,
            HarIncludeSecrets = false,
            Cookies = null,         // matches the command line's default: per-request browser-captured
                                    // cookies are still seeded by the runner itself
            TransportOverride = null,   // each saved request keeps its own transport settings
            HarEntries = null,
            Note = line => { lock (_noteLock) _pendingNotes.Add(line); },
            Debug = null
        };

        var progress = new Progress<ChainRunProgress>(p =>
        {
            List<string> notes;
            lock (_noteLock) { notes = new List<string>(_pendingNotes); _pendingNotes.Clear(); }
            int idx = p.Number - 1;
            var completed = StepRow.Completed(p.Number, _rows[idx].Label, p.Outcome, notes);
            bool wasSelected = ReferenceEquals(ResultsGrid.SelectedItem, _rows[idx]);
            _rows[idx] = completed;
            if (wasSelected) ResultsGrid.SelectedItem = completed;
            StatusText.Text = $"Step {p.Number} of {p.Total}…";
        });

        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _cts = new CancellationTokenSource();
        try
        {
            var result = await ChainRunner.RunAsync(steps, ctx, vars, progress, _cts.Token);

            foreach (var label in result.SkippedLabels)
            {
                var step = steps.FirstOrDefault(s => s.Label == label);
                if (step is null) continue;
                int idx = step.Number - 1;
                _rows[idx] = StepRow.Skipped(step.Number, _rows[idx].Label);
            }

            Summary = $"Chain “{_chain.Name}”: {result.Passed} passed · {result.Failed} failed" +
                       (result.SkippedLabels.Count > 0 ? $" · {result.SkippedLabels.Count} skipped" : "");
            StatusText.Text = Summary;
        }
        catch (OperationCanceledException)
        {
            int completed = _rows.Count(r => r.Outcome is not null);
            StatusText.Text = $"Stopped. {completed} step(s) completed.";
        }
        catch (Exception ex)
        {
            // A saved request can carry a malformed method or timeout — imported models are not
            // constrained by this window's own controls — and those throw when the request is built
            // rather than coming back as an error response. On async void that would take the whole
            // process down, and this application persists its state on close, so a crash here would
            // lose the very captures and known-good markers the run just produced.
            StatusText.Text = $"The chain stopped: {ex.Message}";
        }
        finally
        {
            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>The variables a step resolves against: the chain's capture environment when it names
    /// one, otherwise whichever environment the application has active. Read out of
    /// <see cref="AppState.Environments"/> — the persisted list a capture actually writes into — and
    /// never out of the window's UI-bound copy, because a value captured by step one may have just
    /// created that environment, and reading the observable copy would miss it entirely.
    /// <para>When the chain names no environment the command line resolves against NO variables at
    /// all, while this window resolves against the environment the user has selected in the combo.
    /// That difference is deliberate — in the app the environment picker is the user's stated choice
    /// and every other send in the window honours it, so a chain run that ignored it would be the
    /// surprising behavior.</para></summary>
    private static Dictionary<string, string> VariablesFor(AppState state, string? envName)
    {
        var env = envName is { } name
            ? state.Environments.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            : state.Environments.FirstOrDefault(e => e.Id == state.ActiveEnvironmentId);

        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (env is null) return vars;
        foreach (var v in env.Variables)
        {
            var key = v.Key?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            vars[key] = v.Value ?? "";
        }
        return vars;
    }

    /// <summary>The saved request a chain step names, searched depth-first through the collections
    /// tree, matched the same way <see cref="ChainRunner"/> itself matches ids — case-insensitively,
    /// since ids are Guid hex written "N" by the collections tree and "n" by a chain — so this
    /// display never calls something "missing" that the runner would happily resolve.</summary>
    private static CollectionNode? FindRequest(IEnumerable<CollectionNode> nodes, string id)
    {
        foreach (var n in nodes)
        {
            if (!n.IsFolder && n.Request is not null && string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))
                return n;
            if (FindRequest(n.Children, id) is { } found) return found;
        }
        return null;
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowDetail(ResultsGrid.SelectedItem as StepRow);

    /// <summary>The selected step's actual response: a meta line, the pretty-printed body (the same
    /// formatter/highlighter pair <c>MainWindow.SetPretty</c> uses), and its notes plus any failing
    /// assertions. A pending or skipped row (no outcome yet) clears the pane rather than leaving it
    /// showing a previous step's response.</summary>
    private void ShowDetail(StepRow? row)
    {
        if (row?.Outcome is not { } outcome)
        {
            MetaText.Text = "";
            BodyRich.Document = SyntaxHighlighter.Build("", BodyKind.Empty);
            NotesBox.Text = "";
            return;
        }

        var response = outcome.Response;
        MetaText.Text = response.Error is not null
            ? response.Error.Message
            : $"{response.StatusCode} {response.ReasonPhrase} · {response.Body.LongLength} bytes · {response.Elapsed.TotalMilliseconds:F0} ms";

        var formatted = new ResponseFormatter().Format(response);
        BodyRich.Document = SyntaxHighlighter.Build(formatted.Text, formatted.Kind);

        var sb = new StringBuilder();
        foreach (var note in row.Notes) sb.AppendLine(note);
        var failing = AssertionEvaluator.Evaluate(outcome.Request.Assertions, response).Where(a => !a.Passed).ToList();
        if (failing.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"FAILED ASSERTIONS ({failing.Count})");
            foreach (var a in failing) sb.AppendLine($"  ✗ {a.Description}   → got {a.Actual ?? "∅"}");
        }
        NotesBox.Text = sb.ToString().TrimEnd();
    }

    /// <summary>One row of the steps list. A row's fields change as the run progresses (pending →
    /// passed/failed/skipped) but <see cref="ObservableCollection{T}"/> only notifies on collection
    /// change, not property change — so rather than make this implement
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/>, a completed step replaces its row
    /// in the collection outright. That mirrors how <c>ChainEditorWindow.RefreshSteps</c> handles the
    /// same non-observable-model problem, and it is simpler than wiring change notification onto a
    /// value that is otherwise immutable once built.
    /// <para>Public because WPF's binding engine reflects over the item type.</para></summary>
    public sealed class StepRow
    {
        private StepRow(string number, string verdict, Brush verdictColor, string statusText, string ms,
                         string sizeText, string label, RequestOutcome? outcome, IReadOnlyList<string> notes)
        {
            Number = number;
            Verdict = verdict;
            VerdictColor = verdictColor;
            StatusText = statusText;
            Ms = ms;
            SizeText = sizeText;
            Label = label;
            Outcome = outcome;
            Notes = notes;
        }

        public string Number { get; }
        public string Verdict { get; }
        public Brush VerdictColor { get; }
        public string StatusText { get; }
        public string Ms { get; }
        public string SizeText { get; }
        public string Label { get; }

        /// <summary>What this step actually did, once it has run. Null before then and for a skipped
        /// step — not bound in the grid, read by the detail pane when this row is selected.</summary>
        internal RequestOutcome? Outcome { get; }

        /// <summary>The notes <c>ctx.Note</c> collected for this step (captures, assertion failures,
        /// warnings), already formatted by the runner. Not bound in the grid.</summary>
        internal IReadOnlyList<string> Notes { get; }

        public static StepRow Pending(int number, string label) =>
            new($"{number}.", "—", Verdicts.Pending, "", "", "", label, null, Array.Empty<string>());

        public static StepRow Completed(int number, string label, RequestOutcome outcome, IReadOnlyList<string> notes)
        {
            var response = outcome.Response;
            string status = response.Error is not null ? "ERR" : response.StatusCode?.ToString() ?? "";
            string ms = response.Error is null ? response.Elapsed.TotalMilliseconds.ToString("F0") : "";
            string size = response.Error is null ? Human(response.Body.LongLength) : "—";
            return new($"{number}.", outcome.Passed ? "PASS" : "FAIL", outcome.Passed ? Verdicts.Pass : Verdicts.Fail,
                       status, ms, size, label, outcome, notes);
        }

        public static StepRow Skipped(int number, string label) =>
            new($"{number}.", "SKIP", Verdicts.Skip, "", "", "", label, null, Array.Empty<string>());

        private static string Human(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:F1} KB" : $"{b / 1048576.0:F1} MB";
    }

    // Verdict text colours, frozen once and reused. Two sets: bright hues for the dark theme, deeper
    // hues that read on white for the light theme — the same idiom FuzzWindow uses for its outcomes.
    private sealed record VerdictPalette(Brush Pending, Brush Pass, Brush Fail, Brush Skip);

    private static readonly VerdictPalette DarkVerdicts = new(
        Pending: Frozen("#8A8F99"), Pass: Frozen("#3DDC97"), Fail: Frozen("#FF6E7A"), Skip: Frozen("#FFC247"));

    private static readonly VerdictPalette LightVerdicts = new(
        Pending: Frozen("#6B6F78"), Pass: Frozen("#167A4E"), Fail: Frozen("#C8324C"), Skip: Frozen("#9A6700"));

    private static VerdictPalette Verdicts =>
        string.Equals((Application.Current as App)?.CurrentTheme, "Light", StringComparison.OrdinalIgnoreCase)
            ? LightVerdicts : DarkVerdicts;

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
