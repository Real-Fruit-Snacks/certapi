namespace ApiTester.Core;

/// <summary>Applies a request's capture rules to a response, writing extracted values into the
/// active environment (auto-creating one named "Captured" when none is active).</summary>
public static class CaptureApplier
{
    public static IReadOnlyList<(string Variable, bool Ok, string? Error)> Apply(
        AppState state, IEnumerable<CaptureRule> rules,
        byte[] body, string? contentType, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var results = new List<(string, bool, string?)>();
        var applicable = rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Variable)).ToList();
        if (applicable.Count == 0) return results;

        ApiEnvironment? env = null;
        foreach (var rule in applicable)
        {
            var (value, error) = ResponseCapture.Extract(rule.ToSpec(), body, contentType, headers);
            if (value is null) { results.Add((rule.Variable.Trim(), false, error)); continue; }
            env ??= ActiveEnvironment(state);
            Upsert(env, rule.Variable.Trim(), value);
            results.Add((rule.Variable.Trim(), true, null));
        }
        return results;
    }

    private static ApiEnvironment ActiveEnvironment(AppState state)
    {
        if (state.ActiveEnvironmentId is { } id &&
            state.Environments.FirstOrDefault(e => e.Id == id) is { } active)
            return active;

        var created = new ApiEnvironment { Name = "Captured" };
        state.Environments.Add(created);
        state.ActiveEnvironmentId = created.Id;
        return created;
    }

    private static void Upsert(ApiEnvironment env, string key, string value)
    {
        // A capture rule's whole purpose is to lift a value out of a response - an access token,
        // a session id - and this is precisely the path that lands credentials in an environment
        // file, so what a capture writes is treated as a secret whether or not the variable existed
        // before.
        // Locked on the collection itself: the desktop app registers that same object with WPF's
        // collection synchronization, so a list bound to it reads safely while a run writes.
        lock (env.Variables)
        {
            var existing = env.Variables.FirstOrDefault(v => v.Key == key);
            if (existing is not null) { existing.Value = value; existing.Secret = true; }
            else env.Variables.Add(new Variable { Key = key, Value = value, Secret = true });
        }
    }
}
