using System.Text;

namespace ApiTester.Core;

/// <summary>Renders a <see cref="DoctorReport"/> as a markdown note.
///
/// <para><b>Why this is worth keeping.</b> A diagnosis is exactly the thing you want a durable
/// record of — it is what gets pasted into a ticket, argued about with a network team, and looked
/// up again six months later when the same host breaks the same way. Today it scrolls off the
/// terminal. The note keeps the parts that are hard to reconstruct: the certificate authorities the
/// server said it accepts, and any TLS-interception finding, verbatim.</para>
///
/// <para><b>Append-only, unlike the catalogue.</b> M1's catalogue is current state, so it is
/// re-exported in place. An investigation is history: each run writes a new note, named for the
/// host and the moment, and nothing ever overwrites a past diagnosis. That asymmetry is deliberate
/// — a vault that quietly replaced last week's failure with this week's would destroy the record
/// exactly when a pattern was becoming visible.</para>
///
/// <para>Pure over the report, so the same diagnosis renders three ways — text, JSON, markdown —
/// with no third source of truth.</para></summary>
public static class DoctorMarkdown
{
    public static string Render(DoctorReport report, DateTimeOffset when, bool includeSecrets = false)
    {
        string url = MarkdownSecrets.RedactUrl(report.Url, includeSecrets);
        // Never the raw URL: one that does not parse can still carry user:password@ or ?api_key=.
        string host = Uri.TryCreate(report.Url, UriKind.Absolute, out var parsed) ? parsed.Host : "unknown-host";
        var failure = report.FirstFailure;

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("tags: [certapi/investigation]");
        sb.AppendLine($"host: {host}");
        sb.AppendLine($"url: {Quote(url)}");
        sb.AppendLine($"outcome: {(report.Ok ? "ok" : "failed")}");
        if (failure is not null) sb.AppendLine($"failedStage: {failure.Name}");
        sb.AppendLine($"ran: {when.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"elapsedMs: {report.Stages.Sum(s => s.Elapsed.TotalMilliseconds):F0}");
        sb.AppendLine("---").AppendLine();

        sb.AppendLine($"# {MarkdownExport.Escape(host)} — {(report.Ok ? "all stages passed" : Headline(failure!))}")
          .AppendLine();
        sb.AppendLine($"`certapi doctor {url}` · {when.ToUniversalTime():yyyy-MM-dd HH:mm} UTC").AppendLine();

        if (report.Stages.Count == 0)
        {
            sb.AppendLine("_No stages ran._");
            return sb.ToString();
        }

        sb.AppendLine("| Stage | Result | Summary | Time |").AppendLine("|---|---|---|---|");
        foreach (var stage in report.Stages)
            sb.AppendLine($"| {MarkdownExport.Cell(stage.Name)} "
                        + $"| {(stage.Ok ? "ok" : "**FAIL**")} "
                        + $"| {MarkdownExport.Cell(Redact(stage.Summary, includeSecrets))} "
                        + $"| {stage.Elapsed.TotalMilliseconds:F0} ms |");
        sb.AppendLine();

        // Only the stages that carry something worth reading get a detail section — a note padded
        // with "ok, nothing to say" is a note nobody scrolls through to the part that matters.
        var interesting = report.Stages.Where(s => !s.Ok || s.Advice is not null || s.Detail.Count > 0).ToList();
        if (interesting.Count > 0)
        {
            sb.AppendLine("## Detail").AppendLine();
            foreach (var stage in interesting)
            {
                sb.AppendLine($"### {MarkdownExport.Escape(stage.Name)} — {(stage.Ok ? "ok" : "FAIL")}").AppendLine();
                sb.AppendLine(MarkdownExport.Escape(Redact(stage.Summary, includeSecrets))).AppendLine();

                // Verbatim: the acceptable-client-CA list and the interception note live here, and
                // they are the whole reason this note is worth keeping. Escaped, never reworded.
                foreach (var line in stage.Detail)
                    sb.AppendLine($"- {MarkdownExport.Escape(Redact(line, includeSecrets))}");
                if (stage.Detail.Count > 0) sb.AppendLine();

                if (stage.Advice is not null)
                    sb.AppendLine($"> {MarkdownExport.Escape(stage.Advice)}").AppendLine();
            }
        }

        sb.AppendLine("## Outcome").AppendLine();
        sb.AppendLine(report.Ok
            ? $"Every stage passed, in {report.Stages.Sum(s => s.Elapsed.TotalMilliseconds):F0} ms total."
            : $"Stopped at **{MarkdownExport.Escape(failure!.Name)}**: "
              + MarkdownExport.Escape(Redact(failure.Summary, includeSecrets)));
        return sb.ToString();
    }

    /// <summary>Where a vault note goes: <c>&lt;into&gt;/investigations/&lt;host&gt;-&lt;stamp&gt;.md</c>.
    /// The timestamp is what makes this append-only — two runs against the same host in the same
    /// second would collide, which is why it carries seconds and the caller may pass its own moment.</summary>
    public static string VaultPath(DoctorReport report, DateTimeOffset when, string into = "certapi")
    {
        string host = Uri.TryCreate(report.Url, UriKind.Absolute, out var parsed) ? parsed.Host : "unknown-host";
        string stamp = when.ToUniversalTime().ToString("yyyyMMdd-HHmmss");
        return $"{MarkdownExport.Sanitize(into, "certapi")}/investigations/"
             + $"{MarkdownExport.Sanitize($"{host}-{stamp}", "investigation")}.md";
    }

    /// <summary>The title's second half: what a reader needs to see without opening the note.</summary>
    private static string Headline(DoctorStage failure) =>
        failure.Name switch
        {
            "dns" => "DNS lookup failed",
            "tcp" => "could not connect",
            "connect" => "the proxy refused the tunnel",
            "tls" => "TLS handshake failed",
            "http" => "the HTTP request failed",
            "url" => "the URL is not usable",
            _ => $"{failure.Name} failed"
        };

    /// <summary>A stage line may quote the URL it was working on, so the same redaction applies
    /// here as to the URL itself — otherwise the address would be cleaned in the frontmatter and
    /// leak two lines further down.</summary>
    private static string Redact(string text, bool includeSecrets)
    {
        // The cheap way out is "no URL here at all". It used to be "no '?' here", which was right
        // when a query string was the only way a credential rode along in a URL — and wrong once
        // `user:password@host` counted too, because that form contains no question mark.
        if (includeSecrets || !text.Contains("://", StringComparison.Ordinal)) return text;

        var sb = new StringBuilder(text.Length);
        foreach (string word in text.Split(' '))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(word.Contains("://", StringComparison.Ordinal)
                ? MarkdownSecrets.RedactUrl(word)
                : word);
        }
        return sb.ToString();
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
