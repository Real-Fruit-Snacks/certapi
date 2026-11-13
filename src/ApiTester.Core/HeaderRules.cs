namespace ApiTester.Core;

/// <summary>The header rules named on `certapi serve`'s four repeatable flags — set (replace or
/// add) and remove, independently for the request side and the response side. This works with or
/// without --browser: it is not a browser concern, so it lives in its own pure, UI-free,
/// socket-free type rather than inside <see cref="BrowserRewriter"/>'s browser-mode gating, and the
/// `serve` command applies it on the way in and out, regardless of browser mode — <see
/// cref="MtlsGateway"/> itself stays a faithful byte relay and knows nothing about these rules.
/// There is no public constructor:
/// <see cref="TryCreate"/> is the only way to build one, so a rule set that exists has already
/// passed validation.</summary>
public sealed class HeaderRules
{
    /// <summary>The header names a user may not set or remove, matched case-insensitively.
    /// Deliberately its own set rather than a reference to <c>MtlsGateway.HopByHop</c>: that set
    /// answers "never relay this through a proxy" and this one answers "a user may not manage this
    /// on the command line" — today the two judgements happen to name the same ten headers, but
    /// that is not a reason to couple them, since a future change to one must not silently change
    /// the other. They must nonetheless not diverge in one direction: <c>MtlsGateway.ForwardAsync</c>
    /// strips <c>HopByHop</c> names *after* these rules have already been applied, so a name in
    /// <c>HopByHop</c> that is not refused here is a rule <c>serve</c> accepts on the command line
    /// and then silently throws away. <c>HeaderRulesTests.Every_hop_by_hop_name_is_also_one_a_user_may_not_manage</c>
    /// pins <c>HopByHop</c> as a subset of this set so that trap cannot reopen unnoticed.</summary>
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "TE", "Trailer",
        "Upgrade", "Proxy-Authenticate", "Proxy-Authorization", "Host"
    };

    private readonly Direction _request;
    private readonly Direction _response;

    private HeaderRules(
        IReadOnlyList<KeyValuePair<string, string>> setRequest,
        IReadOnlyList<string> removeRequest,
        IReadOnlyList<KeyValuePair<string, string>> setResponse,
        IReadOnlyList<string> removeResponse)
    {
        _request = new Direction(setRequest, removeRequest);
        _response = new Direction(setResponse, removeResponse);
    }

    /// <summary>The rules named on the command line, or null with <paramref name="problem"/> set to
    /// the usage error when a rule names a header the gateway refuses to let a user manage.</summary>
    public static HeaderRules? TryCreate(
        IReadOnlyList<KeyValuePair<string, string>> setRequest,
        IReadOnlyList<string> removeRequest,
        IReadOnlyList<KeyValuePair<string, string>> setResponse,
        IReadOnlyList<string> removeResponse,
        out string? problem)
    {
        // Scanned in the order the flags are documented — request before response, set before
        // remove within each — so the first bad name a user would notice reading their own command
        // line back is the one named in the error.
        problem = FirstProblem(setRequest.Select(h => h.Key), "--request-header", "\"Name: value\"")
                  ?? FirstProblem(removeRequest, "--remove-request-header", "<name>")
                  ?? FirstProblem(setResponse.Select(h => h.Key), "--response-header", "\"Name: value\"")
                  ?? FirstProblem(removeResponse, "--remove-response-header", "<name>")
                  ?? FirstValueProblem(setRequest, "--request-header")
                  ?? FirstValueProblem(setResponse, "--response-header");
        if (problem is not null) return null;

        return new HeaderRules(setRequest, removeRequest, setResponse, removeResponse);
    }

    /// <summary>The usage error for the first bad name in <paramref name="names"/>, or null when
    /// every name in this list is one the gateway can actually apply a rule against. A name can be
    /// bad three ways, checked in this order: missing (empty or whitespace-only), spelled with a
    /// character an HTTP field name cannot carry, or naming a header this gateway refuses to let a
    /// user manage. <paramref name="form"/> is how this flag's argument is written, so the "missing"
    /// message can show the shape the user should have typed instead.</summary>
    private static string? FirstProblem(IEnumerable<string> names, string flag, string form)
    {
        foreach (var name in names)
        {
            // An empty or whitespace-only name used to be the one failure that slipped all the way
            // through to the forwarding path, where TryAddWithoutValidation("", value) silently drops
            // the header and leaves the operator believing their rule applied. Refusing it here, at
            // the single gate every rule set passes through, keeps the promise the other refusals
            // already make: a rule that is accepted is a rule that takes effect.
            if (string.IsNullOrWhiteSpace(name))
                return $"{flag} needs a header name: expected {form}.";

            foreach (var c in name)
            {
                if (IsFieldNameChar(c)) continue;
                return $"{flag} cannot name '{name}': '{c}' is not legal in an HTTP field name, so " +
                       "the header could never match and the rule would be dropped rather than " +
                       "applied. A name may hold letters, digits and !#$%&'*+-.^_`|~ only.";
            }

            if (!Refused.Contains(name)) continue;
            // Host gets its own message: the other nine are refused because the HTTP stack frames
            // the message with them, but Host is refused for an unrelated reason — the client sets
            // it from the upstream URI, so a rule here could never actually take effect.
            return name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                ? $"{flag} cannot name 'Host': the gateway's HTTP client sets it from the upstream " +
                  "URI, so a rule here would only half-apply — and a half-working override is worse " +
                  "than none."
                : $"{flag} cannot name '{name}': it frames the HTTP message and the HTTP stack " +
                  "manages it, so a rule here would corrupt the exchange.";
        }
        return null;
    }

    /// <summary>A value with CR, LF or NUL would be written as further header lines on the wire.</summary>
    private static string? FirstValueProblem(IEnumerable<KeyValuePair<string, string>> rules, string flag)
    {
        foreach (var r in rules)
            if (HeaderValues.HasLineBreak(r.Value))
                return $"{flag} '{r.Key}': the value contains a line break or NUL character, which would be sent as another header line.";
        return null;
    }

    /// <summary>Whether <paramref name="c"/> is legal within an HTTP field name — the `token`
    /// production of RFC 9110 §5.1 (https://www.rfc-editor.org/rfc/rfc9110#section-5.1), restricted
    /// to ASCII since a header name is never anything else.</summary>
    private static bool IsFieldNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);

    /// <summary>The request headers with this rule set's request-side rules applied. Returns the
    /// very same list instance when there are no request rules, so the default relay is provably
    /// byte-faithful; otherwise a new list, and the input is never mutated.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ApplyToRequest(
        IReadOnlyList<KeyValuePair<string, string>> headers) => Apply(headers, _request);

    /// <summary>The response headers with this rule set's response-side rules applied. Returns the
    /// very same list instance when there are no response rules; otherwise a new list, and the
    /// input is never mutated.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ApplyToResponse(
        IReadOnlyList<KeyValuePair<string, string>> headers) => Apply(headers, _response);

    private static IReadOnlyList<KeyValuePair<string, string>> Apply(
        IReadOnlyList<KeyValuePair<string, string>> headers, Direction rules)
    {
        // Nothing to do for this direction: handing back the exact instance we were given (rather
        // than an equal copy) is what lets a caller prove the default relay never touches a header
        // at all, not merely that it produces the same values.
        if (!rules.HasRules) return headers;

        var sets = rules.Sets;
        var result = new List<KeyValuePair<string, string>>(headers.Count + sets.Length);
        // Which set rules have already been written — whether by replacing an original occurrence
        // in place or by appending below — so a repeated original occurrence of a set name
        // collapses to one, and a set rule already satisfied by an original header is not appended
        // again. Indexed by rule rather than keyed by name, because Direction has already reduced
        // the rules to one entry per name.
        var written = new bool[sets.Length];

        foreach (var h in headers)
        {
            if (rules.Removals.Contains(h.Key)) continue;   // removal already beat any set rule
            if (rules.IndexByName.TryGetValue(h.Key, out int at))
            {
                // The first occurrence keeps its position but takes the rule's value and the rule's
                // spelling of the name — the user asked for that header, so that is what is written.
                if (!written[at])
                {
                    written[at] = true;
                    result.Add(sets[at]);
                }
                continue;   // every later occurrence of this name is dropped
            }
            result.Add(h);
        }

        // Any set rule not already satisfied above — the header was simply never present — is new
        // and is appended, in the order the rules were given.
        for (int i = 0; i < sets.Length; i++)
            if (!written[i]) result.Add(sets[i]);

        return result;
    }

    /// <summary>One direction's rules reduced, once, to the form <see cref="Apply"/> wants. Both
    /// precedence decisions are settled here rather than per call: removal beats setting, so a name
    /// on both lists never reaches <see cref="Sets"/> at all, and a name set more than once keeps
    /// only the last rule, because that is the last thing the user said. The rules cannot change
    /// after construction, so rebuilding these lookups on every proxied request would be recomputing
    /// a constant — the per-request work is then just the walk over the headers themselves.</summary>
    private sealed class Direction
    {
        /// <summary>The surviving set rules, one per name, each at the position of that name's first
        /// mention and carrying the last value given for it.</summary>
        public readonly KeyValuePair<string, string>[] Sets;

        /// <summary>Where each set name sits in <see cref="Sets"/>, matched case-insensitively.</summary>
        public readonly Dictionary<string, int> IndexByName;

        /// <summary>The names to strip, matched case-insensitively.</summary>
        public readonly HashSet<string> Removals;

        public Direction(IReadOnlyList<KeyValuePair<string, string>> set, IReadOnlyList<string> remove)
        {
            Removals = new HashSet<string>(remove, StringComparer.OrdinalIgnoreCase);
            IndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var sets = new List<KeyValuePair<string, string>>(set.Count);
            foreach (var rule in set)
            {
                if (Removals.Contains(rule.Key)) continue;               // removal wins over setting
                if (IndexByName.TryGetValue(rule.Key, out int at)) sets[at] = rule;   // last value wins
                else
                {
                    IndexByName[rule.Key] = sets.Count;                  // first mention fixes the position
                    sets.Add(rule);
                }
            }
            Sets = sets.ToArray();
        }

        /// <summary>False when this direction carries no rule at all, which is what lets Apply hand
        /// its input straight back.</summary>
        public bool HasRules => Sets.Length > 0 || Removals.Count > 0;
    }
}
