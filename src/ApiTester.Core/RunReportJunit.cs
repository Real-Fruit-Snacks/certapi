using System.Globalization;
using System.Xml.Linq;

namespace ApiTester.Core;

/// <summary>Renders a run as JUnit XML, the test-report format CI systems (Azure DevOps, GitHub
/// Actions reporters, GitLab, Jenkins) show as a test tab without any plugin of certapi's own.
/// One test case per request, a failure per failed request naming each failed assertion with what
/// arrived instead, and a skipped case for each chain step an earlier failure stopped. URLs are
/// redacted the way every other export redacts them. Pure over the report.</summary>
public static class RunReportJunit
{
    public static string Render(RunReport report, DateTimeOffset when, bool includeSecrets = false)
    {
        string suiteName = report.ChainName is { Length: > 0 } chain ? $"certapi chain: {chain}" : "certapi run";
        int failures = report.Rows.Count(r => !r.Passed && r.Error is null);
        int errors = report.Rows.Count(r => !r.Passed && r.Error is not null);

        var suite = new XElement("testsuite",
            new XAttribute("name", Clean(suiteName)),
            new XAttribute("tests", report.Rows.Count + report.Skipped.Count),
            new XAttribute("failures", failures),
            new XAttribute("errors", errors),
            new XAttribute("skipped", report.Skipped.Count),
            new XAttribute("time", Seconds(report.Elapsed)),
            new XAttribute("timestamp", when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));

        foreach (var row in report.Rows)
        {
            string url = includeSecrets ? row.Url : MarkdownSecrets.RedactUrl(row.Url);
            var testCase = new XElement("testcase",
                new XAttribute("name", Clean(row.Label)),
                new XAttribute("classname", Clean(ClassName(row.Label))),
                new XAttribute("time", Seconds(row.Elapsed)));
            if (row.Error is not null)
            {
                testCase.Add(new XElement("error",
                    new XAttribute("message", Clean(row.Error)),
                    new XAttribute("type", "request"),
                    Clean($"{row.Method} {url}\n{row.Error}")));
            }
            else if (!row.Passed)
            {
                var failed = row.Assertions.Where(a => !a.Passed).ToList();
                string message = failed.Count == 0
                    ? $"status {row.Status?.ToString(CultureInfo.InvariantCulture) ?? "none"}"
                    : string.Join("; ", failed.Select(a => a.Description));
                string detail = $"{row.Method} {url} -> {row.Status?.ToString(CultureInfo.InvariantCulture) ?? "no status"}\n" +
                    string.Join("\n", failed.Select(a => $"{a.Description}: got {a.Actual ?? "(nothing)"}"));
                testCase.Add(new XElement("failure", new XAttribute("message", Clean(message)), new XAttribute("type", "assertion"), Clean(detail)));
            }
            testCase.Add(new XElement("system-out", Clean($"{row.Method} {url} -> {row.Status?.ToString(CultureInfo.InvariantCulture) ?? "no status"} in {row.Elapsed.TotalMilliseconds:F0} ms, {row.SizeBytes} bytes")));
            suite.Add(testCase);
        }
        foreach (var label in report.Skipped)
            suite.Add(new XElement("testcase",
                new XAttribute("name", Clean(label)), new XAttribute("classname", Clean(ClassName(label))), new XAttribute("time", "0"),
                new XElement("skipped", new XAttribute("message", "an earlier step failed"))));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("testsuites",
                new XAttribute("name", Clean(suiteName)),
                new XAttribute("tests", report.Rows.Count + report.Skipped.Count),
                new XAttribute("failures", failures),
                new XAttribute("errors", errors),
                new XAttribute("time", Seconds(report.Elapsed)),
                suite));
        using var writer = new Utf8StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    /// <summary>The folder part of a saved request's path, dotted, so a CI report groups requests
    /// the way the collection does: "auth/login" is class "auth", test "auth/login".</summary>
    private static string ClassName(string label)
    {
        int slash = label.LastIndexOf('/');
        return slash <= 0 ? "certapi" : "certapi." + label[..slash].Replace('/', '.');
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>XML 1.0 cannot carry most control characters at all, even escaped.</summary>
    private static string Clean(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); continue; }
            if (System.Xml.XmlConvert.IsXmlChar(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }
}
