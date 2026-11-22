using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Builds a colored FlowDocument for JSON/XML using the RFS Nightshift/Dayshift palette. Two
/// token palettes are kept — bright hues for the dark theme, deeper hues that read on white for the
/// light theme — and the active one is chosen per render from <see cref="App.CurrentTheme"/>.</summary>
internal static class SyntaxHighlighter
{
    private static readonly FontFamily MonoFont = new("Consolas");

    private static SolidColorBrush B(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private sealed record Palette(
        SolidColorBrush Key, SolidColorBrush String, SolidColorBrush Number, SolidColorBrush Literal,
        SolidColorBrush Punct, SolidColorBrush Default, SolidColorBrush Tag, SolidColorBrush Attr);

    private static readonly Palette Dark = new(
        Key: B("#5CB8FF"), String: B("#3DDC97"), Number: B("#FFC247"), Literal: B("#B78CFF"),
        Punct: B("#5E636D"), Default: B("#C4C3BE"), Tag: B("#5CB8FF"), Attr: B("#FF6A3D"));

    private static readonly Palette Light = new(
        Key: B("#0B5FA5"), String: B("#167A4E"), Number: B("#9A6700"), Literal: B("#6D3BD1"),
        Punct: B("#8E929B"), Default: B("#3A3C42"), Tag: B("#0B5FA5"), Attr: B("#C43F11"));

    private static Palette Current =>
        string.Equals((Application.Current as App)?.CurrentTheme, "Light", StringComparison.OrdinalIgnoreCase)
            ? Light : Dark;

    /// <summary>Text longer than this is shown in one color: each token is a Run of its own, and a
    /// RichTextBox lays out tens of thousands of them slowly enough to stall the window.</summary>
    public const int MaxHighlightedChars = 64_000;

    public static FlowDocument Build(string text, BodyKind kind)
    {
        var pal = Current;
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        text = ResponseView.Cut(text, ResponseView.MaxPrettyChars);

        if (text.Length > MaxHighlightedChars || kind is not (BodyKind.Json or BodyKind.Xml))
            paragraph.Inlines.Add(new Run(text) { Foreground = pal.Default });
        else
        {
            var runs = new List<Run>(Math.Min(text.Length / 12 + 4, 4096));
            if (kind == BodyKind.Json) HighlightJson(runs, text, pal);
            else HighlightXml(runs, text, pal);
            paragraph.Inlines.AddRange(runs);
        }

        var doc = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = MonoFont,
            FontSize = 12.5
        };
        return doc;
    }

    private sealed class RunBatcher
    {
        private readonly List<Run> _runs;
        private readonly StringBuilder _sb = new(128);
        private SolidColorBrush? _brush;

        public RunBatcher(List<Run> runs) => _runs = runs;

        public void Append(string source, int start, int length, SolidColorBrush brush)
        {
            if (length <= 0) return;
            if (_brush is not null && !ReferenceEquals(_brush, brush)) Flush();
            _brush = brush;
            _sb.Append(source, start, length);
        }

        /// <summary>Whitespace carries no visible ink, so merging it into the active run avoids
        /// creating thousands of extra WPF Run elements for pretty-printed JSON indentation.</summary>
        public void AppendWhitespace(string source, int start, int length, SolidColorBrush fallback)
        {
            if (length <= 0) return;
            _brush ??= fallback;
            _sb.Append(source, start, length);
        }

        public void Flush()
        {
            if (_sb.Length == 0 || _brush is null) return;
            _runs.Add(new Run(_sb.ToString()) { Foreground = _brush });
            _sb.Clear();
            _brush = null;
        }
    }

    private static void HighlightJson(List<Run> runs, string s, Palette pal)
    {
        var batch = new RunBatcher(runs);
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                int start = i++;
                while (i < n && char.IsWhiteSpace(s[i])) i++;
                batch.AppendWhitespace(s, start, i - start, pal.Default);
            }
            else if (c == '"')
            {
                int start = i++;
                while (i < n)
                {
                    if (s[i] == '\\') { i = Math.Min(n, i + 2); continue; }
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
                int j = i;
                while (j < n && char.IsWhiteSpace(s[j])) j++;
                bool isKey = j < n && s[j] == ':';
                batch.Append(s, start, i - start, isKey ? pal.Key : pal.String);
            }
            else if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                int start = i++;
                while (i < n && s[i] is '{' or '}' or '[' or ']' or ':' or ',') i++;
                batch.Append(s, start, i - start, pal.Punct);
            }
            else if ((uint)(c - '0') <= 9 || (c == '-' && i + 1 < n && (uint)(s[i + 1] - '0') <= 9))
            {
                int start = i++;
                while (i < n && ((uint)(s[i] - '0') <= 9 || s[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                batch.Append(s, start, i - start, pal.Number);
            }
            else if ((c == 't' && Matches(s, i, "true")) ||
                     (c == 'f' && Matches(s, i, "false")) ||
                     (c == 'n' && Matches(s, i, "null")))
            {
                int len = c == 'f' ? 5 : 4;
                batch.Append(s, i, len, pal.Literal);
                i += len;
            }
            else
            {
                int start = i++;
                while (i < n)
                {
                    char x = s[i];
                    if (char.IsWhiteSpace(x) || x is '"' or '{' or '}' or '[' or ']' or ':' or ',' or '-'
                        || (uint)(x - '0') <= 9
                        || (x == 't' && Matches(s, i, "true"))
                        || (x == 'f' && Matches(s, i, "false"))
                        || (x == 'n' && Matches(s, i, "null")))
                        break;
                    i++;
                }
                batch.Append(s, start, i - start, pal.Default);
            }
        }
        batch.Flush();
    }

    private static bool Matches(string s, int i, string word) =>
        i + word.Length <= s.Length && s.AsSpan(i, word.Length).SequenceEqual(word);

    private static void HighlightXml(List<Run> runs, string s, Palette pal)
    {
        var batch = new RunBatcher(runs);
        int i = 0, n = s.Length;
        while (i < n)
        {
            if (s[i] == '<')
            {
                int start = i++;
                while (i < n && s[i] != '>') i++;
                if (i < n) i++;
                HighlightXmlTag(batch, s, start, i, pal);
            }
            else
            {
                int start = i;
                while (i < n && s[i] != '<') i++;
                batch.Append(s, start, i - start, pal.Default);
            }
        }
        batch.Flush();
    }

    private static void HighlightXmlTag(RunBatcher batch, string s, int i, int end, Palette pal)
    {
        while (i < end)
        {
            char c = s[i];
            if (c is '"' or '\'')
            {
                char quote = c;
                int start = i++;
                while (i < end && s[i] != quote) i++;
                if (i < end) i++;
                batch.Append(s, start, i - start, pal.String);
            }
            else if (c is '<' or '>' or '/' or '?' or '!' or '=')
            {
                int start = i++;
                while (i < end && s[i] is '<' or '>' or '/' or '?' or '!' or '=') i++;
                batch.Append(s, start, i - start, pal.Tag);
            }
            else
            {
                int start = i;
                while (i < end && s[i] is not ('"' or '\'' or '>' or '<' or '=' or '/')) i++;
                if (i == start) i++;
                batch.Append(s, start, i - start, pal.Attr);
            }
        }
    }
}
