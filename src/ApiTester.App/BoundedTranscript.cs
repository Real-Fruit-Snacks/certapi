using System.Windows.Controls;

namespace ApiTester.App;

/// <summary>Appends lines to a read-only log TextBox and keeps it to a bounded size.
/// <para>A stream or a busy mock server adds a line per message. Left to grow, the TextBox held the
/// whole session, and each append and scroll laid out more of it on the UI thread, until the window
/// stopped responding; one large message did the same at once. Here a line is cut at
/// <see cref="MaxLineChars"/>, and once the text passes <see cref="MaxChars"/> the oldest half is
/// dropped, with a line saying so.</para></summary>
public sealed class BoundedTranscript
{
    public const int MaxChars = 512 * 1024;
    public const int MaxLineChars = 64 * 1024;

    private readonly TextBox _box;
    private int _chars;

    public BoundedTranscript(TextBox box)
    {
        _box = box;
        // Otherwise the undo stack keeps every append, and the text it holds is not bounded at all.
        _box.IsUndoEnabled = false;
    }

    public void Append(string line)
    {
        if (line.Length > MaxLineChars)
            line = line[..MaxLineChars] + $" … ({line.Length - MaxLineChars:N0} more characters not shown)";
        line += Environment.NewLine;
        _box.AppendText(line);
        _chars += line.Length;
        if (_chars > MaxChars) DropOldest();
        _box.ScrollToEnd();
    }

    public void Clear()
    {
        _box.Clear();
        _chars = 0;
    }

    private void DropOldest()
    {
        string text = _box.Text;
        int from = text.IndexOf('\n', Math.Max(0, text.Length - MaxChars / 2));
        string kept = $"… earlier lines removed (this window keeps roughly the last {MaxChars / 2 / 1024} KB).{Environment.NewLine}" +
                      (from < 0 ? "" : text[(from + 1)..]);
        _box.Text = kept;
        _chars = kept.Length;
    }
}
