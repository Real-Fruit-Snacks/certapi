using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

/// <summary>A compiled `FileDescriptorSet` — the binary output of
/// `protoc --descriptor_set_out=&lt;file&gt; --include_imports &lt;proto&gt;`, the same interchange
/// format grpcurl's <c>-protoset</c> consumes — is exactly the message-shaped container of the same
/// <see cref="FileDescriptorProto"/>s <see cref="DescriptorPool"/> already rebuilds from server
/// reflection bytes. So the supported path here is to feed the existing pool
/// (<see cref="DescriptorPool.FromDescriptorSet"/>) rather than grow a second descriptor-building
/// path alongside it. This type deliberately exposes no Google.Protobuf type on its public surface,
/// so ApiTester.Cli still never has to reference Protobuf directly — the same containment promise
/// <see cref="GrpcCaller"/> already keeps for the reflection path.</summary>
public sealed partial class GrpcDescriptorSet
{
    /// <summary>The path the set was loaded from, as the user wrote it — used in messages.</summary>
    public string SourcePath { get; }

    /// <summary>Every service the descriptor set declares, each with its methods. Computed at load
    /// time from real descriptors, so this needs no server, no address and no connection.</summary>
    public IReadOnlyList<GrpcServiceInfo> Services { get; }

    internal DescriptorPool Pool { get; }

    private GrpcDescriptorSet(string sourcePath, DescriptorPool pool, IReadOnlyList<GrpcServiceInfo> services)
    {
        SourcePath = sourcePath;
        Pool = pool;
        Services = services;
    }

    /// <summary>Reads and validates a compiled FileDescriptorSet — the binary output of
    /// `protoc --descriptor_set_out=&lt;file&gt; --include_imports &lt;proto&gt;`, the same interchange
    /// format grpcurl's -protoset consumes. Throws <see cref="GrpcDescriptorSetException"/> for
    /// every user-facing problem (missing file, unreadable file, unparseable content, a set that
    /// declares no files), so the command layer has exactly one exception type to map to exit 3.</summary>
    public static GrpcDescriptorSet Load(string path)
    {
        if (!File.Exists(path))
            throw new GrpcDescriptorSetException($"Descriptor set file not found: {path}");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GrpcDescriptorSetException($"Could not read the descriptor set file '{path}': {ex.Message}");
        }

        FileDescriptorSet set;
        try
        {
            set = FileDescriptorSet.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException)
        {
            if (LooksLikeProtoSource(bytes))
            {
                throw new GrpcDescriptorSetException(
                    $"'{path}' looks like a .proto source file, not a compiled descriptor set. " +
                    $"Compile it first: protoc --descriptor_set_out=<file> --include_imports {path}");
            }
            throw new GrpcDescriptorSetException(
                $"'{path}' is not a parseable Protobuf FileDescriptorSet. A descriptor set is the binary output of: " +
                "protoc --descriptor_set_out=<file> --include_imports <proto>.");
        }

        if (set.File.Count == 0)
        {
            // A text file — including a .proto source — can parse "successfully" as an empty
            // FileDescriptorSet, because Protobuf's binary wire format tolerates a lot of garbage as
            // unknown fields when nothing forces a schema mismatch. So this case, and not only the
            // InvalidProtocolBufferException case above, must also check for the .proto-source mistake.
            if (LooksLikeProtoSource(bytes))
            {
                throw new GrpcDescriptorSetException(
                    $"'{path}' looks like a .proto source file, not a compiled descriptor set. " +
                    $"Compile it first: protoc --descriptor_set_out=<file> --include_imports {path}");
            }
            throw new GrpcDescriptorSetException(
                $"'{path}' parsed as a FileDescriptorSet but declares no files. A descriptor set is the binary output " +
                "of: protoc --descriptor_set_out=<file> --include_imports <proto>.");
        }

        // The single most common way to produce a descriptor set that looks fine and is not: running
        // protoc without --include_imports, so the compiled root file is present but the well-known
        // types (or any other transitive dependency) it imports are not. Left unchecked, this fails
        // deep inside DescriptorPool.Rebuild() -> FileDescriptor.BuildFromByteStrings, whose own
        // DescriptorValidationException names neither the forgotten flag nor, usefully, which file is
        // actually missing — leaving the diagnosis entirely to the user. Checking the closure here,
        // before that generic backstop ever runs, lets the message name the exact missing file and the
        // exact fix.
        foreach (var file in set.File)
        {
            foreach (var dependency in file.Dependency)
            {
                if (set.File.Any(f => f.Name == dependency)) continue;

                throw new GrpcDescriptorSetException(
                    $"The descriptor set '{path}' is missing '{dependency}', which '{file.Name}' imports. " +
                    "Re-run protoc with --include_imports so the descriptor set carries the full dependency " +
                    "closure: protoc --descriptor_set_out=<file> --include_imports <proto>.");
            }
        }

        var pool = DescriptorPool.FromDescriptorSet(set.File.ToList(), path);
        return new GrpcDescriptorSet(path, pool, pool.DescribeServices());
    }

    /// <summary>A cheap, deliberately conservative check for the mistake of passing the `.proto`
    /// source instead of the compiled descriptor set. Only ever consulted on a path that has
    /// ALREADY failed to be a usable descriptor set, so a false positive cannot mislead a user
    /// whose file was fine.</summary>
    private static bool LooksLikeProtoSource(byte[] bytes)
    {
        string text;
        try
        {
            int length = Math.Min(bytes.Length, 8192);
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, 0, length);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            // Either genuinely not text, or the 8192-byte slice cut a multi-byte UTF-8 character in
            // half — either way, "not a .proto source" is the correct conservative answer.
            return false;
        }

        return ProtoSourceLine.IsMatch(text);
    }

    // A line (after optional leading whitespace) that opens with one of the handful of top-level
    // .proto keywords. Deliberately loose — this only ever runs on a path that has already failed to
    // parse as a descriptor set, so a false positive cannot mislead a user whose file was fine; it
    // only sharpens the message for a user who is already looking at an error.
    private static readonly Regex ProtoSourceLine = ProtoSourceLineRegex();
    [GeneratedRegex(@"^\s*(syntax\s*=|package\s|import\s|message\s|service\s|enum\s)", RegexOptions.Multiline)]
    private static partial Regex ProtoSourceLineRegex();
}
