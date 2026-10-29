using System.Runtime.InteropServices;
using System.Text;

namespace ApiTester.Core;

/// <summary>Encrypts an individual secret value for storage in the workspace file. Implementations
/// are per-user: what one Windows user protects, another cannot read.</summary>
public interface ISecretProtector
{
    /// <summary>True when this protector can actually encrypt on this system. When false, callers
    /// store secrets in the clear rather than losing them.</summary>
    bool IsAvailable { get; }

    /// <summary>Encrypt <paramref name="plaintext"/> into its stored form (see
    /// <see cref="SecretProtection.Prefix"/>). Returns null when it cannot — the caller then stores
    /// the value unencrypted rather than losing it.</summary>
    string? Protect(string plaintext);

    /// <summary>Decrypt a value produced by <see cref="Protect"/>. Returns null when it cannot be
    /// decrypted: a different Windows user, a different machine, or a corrupt blob.</summary>
    string? Unprotect(string stored);
}

/// <summary>Per-user encryption through the Windows Data Protection API (DPAPI), called directly
/// via crypt32.dll so ApiTester.Core keeps zero package references. Every operation is total: it
/// returns null rather than throwing, on any failure and on any non-Windows platform.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // CRYPTPROTECT_UI_FORBIDDEN: the call can never raise an interactive prompt — the CLI runs
    // headless. Deliberately NOT CRYPTPROTECT_LOCAL_MACHINE (0x4): scope is the current user, not
    // machine-wide — that distinction matters for this release.
    private const int CryptProtectUiForbidden = 0x1;

    // Fixed application entropy: binds a blob to this application's format. It ships in the
    // binary, so it is not a secret, but it must never change for v1 or every value already
    // written to a workspace file becomes undecryptable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ApiTester.Core/secret/v1");

    private bool? _isAvailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            // DPAPI genuinely fails for accounts with no loaded user profile; round-tripping a
            // probe value through the real Protect/Unprotect path is the only honest way to know.
            return _isAvailable ??= ProbeRoundTrips();
        }
    }

    private bool ProbeRoundTrips()
    {
        const string probe = "afk-secret-protection-availability-probe";
        return ProtectCore(probe) is { } stored && UnprotectCore(stored) == probe;
    }

    /// <inheritdoc />
    public string? Protect(string plaintext) => IsAvailable ? ProtectCore(plaintext) : null;

    /// <inheritdoc />
    public string? Unprotect(string stored) =>
        IsAvailable && SecretProtection.LooksProtected(stored) ? UnprotectCore(stored) : null;

    // Never throws: any P/Invoke failure, bad Base64, or marshaling error returns null.
    private static string? ProtectCore(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        IntPtr inPtr = IntPtr.Zero, entropyPtr = IntPtr.Zero, outPtr = IntPtr.Zero;
        try
        {
            inPtr = AllocCopy(plainBytes);
            entropyPtr = AllocCopy(Entropy);
            var inBlob = new DataBlob { cbData = plainBytes.Length, pbData = inPtr };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyPtr };

            bool ok = CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out var outBlob);
            if (!ok) return null;
            outPtr = outBlob.pbData;

            var cipherBytes = new byte[outBlob.cbData];
            if (outBlob.cbData > 0) Marshal.Copy(outBlob.pbData, cipherBytes, 0, outBlob.cbData);
            return SecretProtection.Prefix + Convert.ToBase64String(cipherBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (entropyPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyPtr);
            if (outPtr != IntPtr.Zero) LocalFree(outPtr);
        }
    }

    // Never throws: assumes SecretProtection.LooksProtected(stored) already passed.
    private static string? UnprotectCore(string stored)
    {
        IntPtr inPtr = IntPtr.Zero, entropyPtr = IntPtr.Zero, outPtr = IntPtr.Zero;
        int outLength = 0;
        try
        {
            var cipherBytes = Convert.FromBase64String(stored[SecretProtection.Prefix.Length..]);
            inPtr = AllocCopy(cipherBytes);
            entropyPtr = AllocCopy(Entropy);
            var inBlob = new DataBlob { cbData = cipherBytes.Length, pbData = inPtr };
            var entropyBlob = new DataBlob { cbData = Entropy.Length, pbData = entropyPtr };

            bool ok = CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                CryptProtectUiForbidden, out var outBlob);
            if (!ok) return null;
            outPtr = outBlob.pbData;
            outLength = outBlob.cbData;

            if (outBlob.cbData == 0) return "";
            var plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (entropyPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyPtr);
            if (outPtr != IntPtr.Zero)
            {
                // Zero the decrypted plaintext in native memory before releasing the buffer, so
                // it does not linger in the process's address space.
                if (outLength > 0) Marshal.Copy(new byte[outLength], 0, outPtr, outLength);
                LocalFree(outPtr);
            }
        }
    }

    private static IntPtr AllocCopy(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length == 0 ? 1 : bytes.Length);
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }
}

/// <summary>Hands back the stored form a secret was read from when the same value is saved again,
/// so saving a workspace encrypts only what changed.
/// <para>Each DPAPI call is a call into lsass, and the workspace keeps more than tokens: cookies,
/// credential headers in history, and the body of every known-good response. Re-encrypting all of
/// it on every save put megabytes through lsass for a one-line change. A value is recognised by an
/// HMAC under a key made fresh for the process, so the plaintext itself is not kept here.</para>
/// <para>A save is a pass (<see cref="BeginPass"/>, <see cref="EndPass"/>): within one, each stored
/// form is handed out once, so two fields that hold the same value still get different stored forms,
/// as DPAPI would have given them; and a value no field held in a pass is forgotten at its end. A
/// value whose stored form is over <see cref="LargestRemembered"/> characters is encrypted each time,
/// and past <see cref="Budget"/> characters in all, new values are no longer remembered.</para></summary>
public sealed class ReusingProtector : ISecretProtector
{
    /// <summary>The longest stored form remembered: about 1.5 MB of plaintext.</summary>
    public const int LargestRemembered = 2 * 1024 * 1024;

    /// <summary>All the stored forms remembered at once, in characters (about 64 MB of memory).</summary>
    public const long Budget = 32L * 1024 * 1024;

    private const int FormsPerValue = 8;

    private sealed class Entry
    {
        public readonly List<string> Forms = new(1);
        public long Pass;
    }

    private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _handedOut = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _pass, _size;

    public ReusingProtector(ISecretProtector inner) => Inner = inner;

    /// <summary>The protector that does the encrypting.</summary>
    public ISecretProtector Inner { get; }

    public bool IsAvailable => Inner.IsAvailable;

    /// <summary>How many stored forms are remembered (for tests).</summary>
    public int Remembered { get { lock (_gate) return _entries.Values.Sum(e => e.Forms.Count); } }

    /// <summary>Starts a save.</summary>
    public void BeginPass()
    {
        lock (_gate) { _pass++; _handedOut.Clear(); }
    }

    /// <summary>Ends a save: what it did not ask for is no longer in the workspace.</summary>
    public void EndPass()
    {
        lock (_gate)
        {
            foreach (var (id, entry) in _entries.Where(e => e.Value.Pass < _pass).ToList())
            {
                _entries.Remove(id);
                _size -= entry.Forms.Sum(f => (long)f.Length);
            }
            _handedOut.Clear();
        }
    }

    public string? Protect(string plaintext)
    {
        string id = Id(plaintext);
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Pass = _pass;
                foreach (var form in entry.Forms)
                    if (_handedOut.Add(form)) return form;
            }
        }
        var fresh = Inner.Protect(plaintext);
        if (fresh is not null)
            lock (_gate)
            {
                Remember(id, fresh);
                _handedOut.Add(fresh);
            }
        return fresh;
    }

    public string? Unprotect(string stored)
    {
        var plain = Inner.Unprotect(stored);
        if (plain is not null) lock (_gate) Remember(Id(plain), stored);
        return plain;
    }

    private void Remember(string id, string stored)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            if (stored.Length > LargestRemembered || _size + stored.Length > Budget) return;
            _entries[id] = entry = new Entry();
        }
        entry.Pass = _pass;
        if (entry.Forms.Contains(stored) || entry.Forms.Count >= FormsPerValue ||
            stored.Length > LargestRemembered || _size + stored.Length > Budget) return;
        entry.Forms.Add(stored);
        _size += stored.Length;
    }

    private string Id(string plaintext) =>
        Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(plaintext)));
}

/// <summary>The protector for a system that cannot encrypt: everything returns null, so callers
/// fall back to storing plaintext instead of losing the value.</summary>
public sealed class UnavailableSecretProtector : ISecretProtector
{
    public bool IsAvailable => false;
    public string? Protect(string plaintext) => null;
    public string? Unprotect(string stored) => null;
}

/// <summary>Marker and default-protector helpers shared by every caller that stores a secret.</summary>
public static class SecretProtection
{
    /// <summary>The marker a stored secret carries. Everything after it is the protector's business;
    /// a value without it is legacy plaintext.</summary>
    public const string Prefix = "enc:v1:";

    /// <summary>The protector this machine uses: DPAPI on Windows, none anywhere else.</summary>
    public static ISecretProtector Default { get; } =
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new UnavailableSecretProtector();

    /// <summary>True when <paramref name="value"/> is in the stored (encrypted) form: the marker
    /// followed by valid Base64 decoding to at least 32 bytes.</summary>
    public static bool LooksProtected(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        // The marker is the only thing that distinguishes ciphertext from a legacy plaintext
        // value, so requiring real Base64 of a plausible length keeps a user's literal password
        // that happens to begin with "enc:v1:" from being mistaken for ciphertext.
        var remainder = value[Prefix.Length..];
        var buffer = new byte[remainder.Length]; // decoded length is always <= encoded length
        return Convert.TryFromBase64String(remainder, buffer, out int bytesWritten) && bytesWritten >= 32;
    }
}
