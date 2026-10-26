using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ApiTester.Core;

/// <summary>How hard to check whether a server's certificate has been revoked by its issuer.
/// <see cref="None"/> is the default everywhere: today, nothing in this client ever asks .NET to
/// check revocation, so a new build must keep asking for exactly nothing until an operator opts in.</summary>
public enum RevocationMode
{
    /// <summary>No revocation checking at all — the historical behavior.</summary>
    None,
    /// <summary>Consult cached certificate revocation lists (CRLs) only; never reach out to the network.</summary>
    Offline,
    /// <summary>Consult cached lists and, if needed, fetch a fresh certificate revocation list (CRL)
    /// or query an Online Certificate Status Protocol (OCSP) responder.</summary>
    Online
}

/// <summary>What a revocation check reported back, independent of whether the connection was
/// ultimately accepted. A trust decision and a revocation finding are different questions, and a
/// caller building diagnostics or an error kind needs to see both.</summary>
public enum RevocationStatus
{
    /// <summary>No checking was asked for (<see cref="RevocationMode.None"/>) — today's behavior.</summary>
    NotChecked,
    /// <summary>Checking ran and the chain reported no revocation problem.</summary>
    Checked,
    /// <summary>The chain says a certificate in it was revoked by its issuer.</summary>
    Revoked,
    /// <summary>Checking was asked for, but the answer could not be obtained — an unreachable or
    /// blocked certificate revocation list (CRL) or Online Certificate Status Protocol (OCSP)
    /// endpoint. This is the ordinary state of affairs on a locked-down corporate network, so it
    /// must never be fatal on its own unless the caller opted into <c>--revocation-strict</c>.</summary>
    Unknown,
    /// <summary><c>--insecure</c> accepted the certificate without enforcing the revocation check
    /// that was asked for. Distinct from <see cref="NotChecked"/> so diagnostics never let a reader
    /// assume revocation was clean when it was in fact never enforced.</summary>
    NotEnforced
}

/// <summary>The outcome of validating one server certificate. <paramref name="RefusedByRevocation"/>
/// is the field callers use to pick an error kind and message: it is non-null exactly when
/// revocation itself is what refused the connection, and null both when the certificate was
/// accepted and when it was refused for an ordinary trust reason (expired, untrusted root, name
/// mismatch) that has nothing to do with revocation.</summary>
public readonly record struct ServerCertificateDecision(
    bool Accepted,
    RevocationStatus Status,
    RevocationStatus? RefusedByRevocation);

/// <summary>The one place every server-certificate validation callback — <c>ApiClient</c>'s send
/// path, <c>MtlsGateway</c>'s serve path, and <c>GrpcChannelFactory</c>'s gRPC path — learns the
/// revocation mode and decides what a chain's revocation flags mean, exactly as
/// <see cref="ProxyConfiguration"/> is the one place all three learn how to reach a server. A
/// genuinely revoked certificate needs a real public-key infrastructure and cannot be produced in a
/// unit test, so every interesting decision is concentrated in <see cref="Decide"/>, a pure function
/// of flags any test can construct by hand, with no network and no certificates required.</summary>
public static class RevocationCheck
{
    /// <summary>Shown when a chain reports a certificate was revoked by its issuer. Deliberately
    /// distinct from an ordinary "untrusted" message: a revoked certificate usually means a private
    /// key was compromised, not merely that a root is missing, and a pinned thumbprint from
    /// <c>certapi trust add</c> does not override it — a pin is an operator's earlier statement,
    /// revocation is the issuer's later withdrawal, and the later statement wins.</summary>
    public const string RevokedMessage =
        "The server's certificate has been REVOKED by its issuer, not merely untrusted. The " +
        "connection was refused. A pinned thumbprint does not override a revocation.";

    /// <summary>Shown when <c>--revocation-strict</c> turned an indeterminate revocation answer into
    /// a refusal. Named so a reader knows dropping the flag is the way back to the tolerant,
    /// corporate-network-friendly default.</summary>
    public const string StrictUnknownMessage =
        "The certificate's revocation status could not be determined, and --revocation-strict " +
        "makes an unknown status fatal. Drop --revocation-strict to continue on an unknown status.";

    /// <summary>Shown when <c>--revocation-strict</c> was given without a revocation mode that could
    /// ever produce an unknown status. Shared by both the command-line validation and the saved-
    /// request validation so the two paths can never drift on the wording.</summary>
    public const string StrictWithoutCheckingMessage =
        "--revocation-strict needs --revocation offline or --revocation online: with revocation " +
        "checking off, there is no unknown status for it to make fatal.";

    /// <summary>Translates this client's three-way mode into the framework's own enum, the same way
    /// <see cref="ProxyConfiguration.Apply"/> translates <see cref="ProxyMode"/> into handler
    /// settings.</summary>
    public static X509RevocationMode ToX509Mode(RevocationMode mode) => mode switch
    {
        RevocationMode.Offline => X509RevocationMode.Offline,
        RevocationMode.Online => X509RevocationMode.Online,
        _ => X509RevocationMode.NoCheck
    };

    /// <summary>Configure how a TLS handshake checks revocation. This is the single place the three
    /// validation paths (<c>ApiClient</c>, <c>MtlsGateway</c>, <c>GrpcChannelFactory</c>) learn the
    /// mode, exactly as <see cref="ProxyConfiguration.Apply"/> is the single place they learn the
    /// proxy — so the three cannot drift apart on how revocation is requested.</summary>
    public static void Apply(SslClientAuthenticationOptions options, RevocationMode mode) =>
        options.CertificateRevocationCheckMode = ToX509Mode(mode);

    /// <summary>As above, and under <see cref="TransportOptions.Offline"/> the chain is built with
    /// certificate downloads disabled: a missing intermediate is a chain error, not a fetch of the
    /// issuer's AIA URL. A chain policy replaces the plain revocation-mode property, so the mode is
    /// carried on the policy.</summary>
    public static void Apply(SslClientAuthenticationOptions options, TransportOptions transport)
    {
        if (!transport.Offline) { Apply(options, transport.Revocation); return; }
        options.CertificateChainPolicy = new X509ChainPolicy
        {
            RevocationMode = ToX509Mode(transport.Revocation),
            DisableCertificateDownloads = true
        };
    }

    /// <summary>Collects every revocation-relevant (and everything else) status flag a built chain
    /// carries. Reads both the chain's own aggregate <see cref="X509Chain.ChainStatus"/> and every
    /// element's <see cref="X509ChainElement.ChainElementStatus"/> and ORs them together: the
    /// aggregate is the normal source, and the per-element scan is cheap insurance against a
    /// platform that only populates one of the two. A null chain — nothing was built — reports
    /// <see cref="X509ChainStatusFlags.NoError"/> rather than throwing, and the whole body is wrapped
    /// in a try/catch for the same reason: an <see cref="X509Chain"/> handed to a validation callback
    /// can be disposed or otherwise unusable, and a diagnostics helper must never be what breaks a
    /// handshake.</summary>
    public static X509ChainStatusFlags ChainFlagsOf(X509Chain? chain)
    {
        if (chain is null) return X509ChainStatusFlags.NoError;

        try
        {
            var flags = X509ChainStatusFlags.NoError;
            foreach (var status in chain.ChainStatus)
                flags |= status.Status;
            foreach (var element in chain.ChainElements)
                foreach (var status in element.ChainElementStatus)
                    flags |= status.Status;
            return flags;
        }
        catch
        {
            return X509ChainStatusFlags.NoError;
        }
    }

    /// <summary>The table every server-certificate validation callback in this product consults.
    /// Pure and side-effect free so every interesting revocation scenario — revoked beating a pin,
    /// an unknown status being swallowed by default but fatal under <c>--revocation-strict</c>,
    /// <c>--insecure</c> overriding but still being reported honestly — can be exercised as plain
    /// data, with no network and no public-key infrastructure needed.</summary>
    public static ServerCertificateDecision Decide(
        RevocationMode mode,
        bool strict,
        bool insecure,
        SslPolicyErrors policyErrors,
        X509ChainStatusFlags chainFlags,
        Func<bool>? pinnedThumbprintMatches)
    {
        // --insecure means "trust anything" and keeps overriding revocation exactly as it overrides
        // every other chain problem. But when checking WAS asked for, silently reporting a clean
        // check would be the tool lying by omission, so the status says plainly it was not
        // enforced. With mode None nothing was asked for in the first place, so NotChecked is the
        // honest answer there instead.
        if (insecure)
        {
            var status = mode == RevocationMode.None ? RevocationStatus.NotChecked : RevocationStatus.NotEnforced;
            return new ServerCertificateDecision(true, status, null);
        }

        // Mode None must reproduce today's behavior byte for byte: accept a clean chain, otherwise
        // let a pinned thumbprint through, otherwise refuse. Nothing about revocation flags is
        // consulted here, even if the platform happens to have populated them anyway — nothing was
        // checked, so nothing about revocation may be claimed.
        if (mode == RevocationMode.None)
        {
            var accepted = policyErrors == SslPolicyErrors.None || (pinnedThumbprintMatches?.Invoke() ?? false);
            return new ServerCertificateDecision(accepted, RevocationStatus.NotChecked, null);
        }

        // Checked before the pin is ever consulted, and it does not matter what else is set in
        // chainFlags or policyErrors: a pin is an operator's earlier statement, revocation is the
        // issuer's later withdrawal, and the later statement always wins. Revoked also wins over an
        // untrusted root in the *reported* status when both flags are present on the same chain.
        if (chainFlags.HasFlag(X509ChainStatusFlags.Revoked))
            return new ServerCertificateDecision(false, RevocationStatus.Revoked, RevocationStatus.Revoked);

        var unknown = chainFlags.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown)
            || chainFlags.HasFlag(X509ChainStatusFlags.OfflineRevocation);

        if (!unknown)
        {
            // Checking ran and found no revocation problem. Anything else wrong with the chain --
            // expired, untrusted root, name mismatch -- is refused by the ordinary rules exactly as
            // before; that is not revocation's business.
            var accepted = policyErrors == SslPolicyErrors.None || (pinnedThumbprintMatches?.Invoke() ?? false);
            return new ServerCertificateDecision(accepted, RevocationStatus.Checked, null);
        }

        if (strict)
        {
            // A pin does not rescue this: the user explicitly asked that an indeterminate answer be
            // fatal, and that is the stricter of the two statements -- stricter than a pin saying
            // "trust it anyway".
            return new ServerCertificateDecision(false, RevocationStatus.Unknown, RevocationStatus.Unknown);
        }

        // The common corporate case: an unreachable or blocked revocation endpoint must not fail an
        // otherwise perfectly good chain, or the tool would be unusable on the networks it targets.
        // But when the chain ALSO has a real problem outside the revocation flags, the ordinary
        // rules -- including the pin -- still decide it, and it was not revocation that refused it,
        // so RefusedByRevocation stays null in every branch below.
        var otherChainProblems = chainFlags & ~(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation);
        var otherPolicyProblems = policyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors;
        var onlyRevocationUnknown = otherChainProblems == X509ChainStatusFlags.NoError && otherPolicyProblems == SslPolicyErrors.None;
        var acceptedDespiteUnknown = onlyRevocationUnknown || (pinnedThumbprintMatches?.Invoke() ?? false);
        return new ServerCertificateDecision(acceptedDespiteUnknown, RevocationStatus.Unknown, null);
    }
}
