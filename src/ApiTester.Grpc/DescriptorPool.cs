using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ApiTester.Grpc;

/// <summary>Turns raw <see cref="FileDescriptorProto"/>s — whether fetched live over server
/// reflection or read whole from a compiled <see cref="FileDescriptorSet"/> — into real
/// <see cref="Google.Protobuf.Reflection"/> descriptors. <see cref="Google.Protobuf.Reflection.FileDescriptorProto"/>
/// is public in Google.Protobuf 3.35.1 (confirmed against the pinned package), so this pool parses
/// bytes with it directly rather than needing the raw-<see cref="CodedInputStream"/> fallback the
/// brief allowed for.</summary>
internal sealed class DescriptorPool
{
    // Far more than any real dependency graph should ever need. A server that keeps introducing
    // filenames past this bound is either malicious or broken, and failing loudly beats hanging.
    private const int MaxFilesFetched = 100;

    // Null in protoset mode: every descriptor is already in hand at construction time, so there is
    // nothing left to fetch and no reflection client is ever consulted. Non-null in the mode
    // GrpcCaller already uses, where descriptors arrive lazily as FindServiceAsync is called.
    private readonly ReflectionClient? _reflection;

    // What every user-facing message in this class calls the source of its descriptors, so the
    // same wording is honest whether they came from a live server or a file on disk.
    private readonly string _sourceLabel;

    private readonly Dictionary<string, FileDescriptorProto> _protos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceDescriptor> _services = new(StringComparer.Ordinal);

    // An Any's type URL names a message, not a service, and expanding one needs a by-name lookup over
    // everything reflection has fetched — which is a strictly larger set than any single file's
    // dependency closure (a service's own input/output types and their transitive dependencies),
    // because the payload type an Any carries is not necessarily imported by the message that holds
    // it. This is exactly what makes expansion work for a payload type the response message does not
    // itself import: the server may have advertised that type's file for an entirely unrelated
    // service, and this index is shared across every service this pool has ever resolved.
    private readonly Dictionary<string, MessageDescriptor> _messages = new(StringComparer.Ordinal);

    // Populated by Rebuild(), in the order the topologically sorted files declare them —
    // deterministic, and what DescribeServices() walks for the offline `grpc list --protoset` path.
    private IReadOnlyList<FileDescriptor> _built = Array.Empty<FileDescriptor>();

    /// <summary>The mode GrpcCaller already uses: descriptors are fetched lazily, one server round
    /// trip at a time, as FindServiceAsync is called for each service in turn.</summary>
    internal DescriptorPool(ReflectionClient reflection)
    {
        _reflection = reflection;
        _sourceLabel = "the descriptors the server returned";
    }

    private DescriptorPool(string sourceLabel)
    {
        _reflection = null;
        _sourceLabel = sourceLabel;
    }

    /// <summary>Protoset mode: every descriptor is already in hand, so reflection is never
    /// consulted and no socket is ever opened. <paramref name="files"/> must already be the
    /// complete dependency closure — GrpcDescriptorSet validates that before calling this.</summary>
    internal static DescriptorPool FromDescriptorSet(IReadOnlyList<FileDescriptorProto> files, string sourcePath)
    {
        var pool = new DescriptorPool($"the descriptor set '{sourcePath}'");
        foreach (var file in files) pool.AddProto(file);
        try
        {
            pool.Rebuild();
        }
        catch (GrpcDescriptorSetException)
        {
            // Rebuild() -> TopologicalSort() -> Broken(...) already throws a precise
            // GrpcDescriptorSetException for protoset mode (e.g. a circular dependency); let it pass
            // through untouched rather than re-wrapping it below into a doubly-worded message.
            throw;
        }
        catch (Exception ex)
        {
            // The backstop for everything Rebuild() can throw — most commonly
            // Google.Protobuf.Reflection's own DescriptorValidationException when the closure is
            // well-formed enough to sort but not to build (a missing import). Slice 2 adds a
            // precise, friendlier check ahead of this for the missing-import case; this is the
            // backstop, not the primary message.
            throw new GrpcDescriptorSetException(
                $"The descriptor set '{sourcePath}' could not be built into descriptors: {ex.Message}");
        }
        return pool;
    }

    /// <summary>The descriptor for a fully-qualified service name, built from the file the server
    /// reports plus the transitive closure of its dependencies. Cached: a `grpc call` should cost
    /// one reflection round trip, not one per lookup.
    /// <para>In protoset mode every service was already built into the cache at construction time
    /// (see <see cref="FromDescriptorSet"/>), so a cache miss here means the set genuinely does not
    /// declare the requested service — there is no server left to ask, and none is asked.</para></summary>
    internal async Task<ServiceDescriptor> FindServiceAsync(string serviceFullName, CancellationToken ct)
    {
        if (_services.TryGetValue(serviceFullName, out var cached)) return cached;

        if (_reflection is null)
        {
            var declared = DescribeServices();
            string known = declared.Count == 0 ? "(none)" : string.Join(", ", declared.Select(s => s.Name));
            throw new GrpcDescriptorSetException(
                $"Service '{serviceFullName}' is not in {_sourceLabel}. Services it declares: {known}.");
        }

        var roots = await _reflection.FileContainingSymbolAsync(serviceFullName, ct);
        foreach (var bytes in roots) AddProto(FileDescriptorProto.Parser.ParseFrom(bytes));

        await ResolveDependenciesAsync(ct);

        Rebuild();

        if (_services.TryGetValue(serviceFullName, out var found)) return found;
        throw new GrpcMethodNotFoundException(
            $"Service '{serviceFullName}' was not found among {_sourceLabel} for it.");
    }

    /// <summary>Fetches all missing reflection files for a batch of services and rebuilds the
    /// descriptor graph once instead of once per service (O(S) instead of O(S^2)).</summary>
    internal async Task<IReadOnlyDictionary<string, ServiceDescriptor>> LoadServicesAsync(
        IReadOnlyList<string> serviceFullNames, CancellationToken ct)
    {
        if (_reflection is not null)
        {
            bool addedAny = false;
            foreach (var name in serviceFullNames)
            {
                if (_services.ContainsKey(name)) continue;
                try
                {
                    var roots = await _reflection.FileContainingSymbolAsync(name, ct);
                    foreach (var bytes in roots)
                    {
                        AddProto(FileDescriptorProto.Parser.ParseFrom(bytes));
                        addedAny = true;
                    }
                }
                catch (GrpcMethodNotFoundException)
                {
                    // Same tolerance as DiscoverAsync's per-service lookup: a service whose file
                    // reflection refuses to hand back stays absent and renders with empty methods.
                }
            }
            if (addedAny)
            {
                await ResolveDependenciesAsync(ct);
                Rebuild();
            }
        }
        return _services;
    }

    /// <summary>Resolves a message by its full name across every file collected so far — the
    /// production resolver behind google.protobuf.Any expansion. Returns null when the name is not
    /// among the descriptors collected so far, which is the caller's cue to fall back to the
    /// visible base64 rendering rather than fail the call (see ProtoJsonReader/Writer's Any
    /// handling).</summary>
    internal MessageDescriptor? FindMessage(string fullName) =>
        _messages.TryGetValue(fullName, out var found) ? found : null;

    /// <summary>The one projection from a built descriptor to the public record shape, shared by
    /// the reflection listing and the descriptor-set listing.</summary>
    internal static GrpcServiceInfo Describe(ServiceDescriptor service) =>
        new(service.FullName, service.Methods.Select(m => new GrpcMethodInfo(
            m.Name, m.IsClientStreaming, m.IsServerStreaming, m.InputType.FullName, m.OutputType.FullName)).ToList());

    /// <summary>Every service the pool has built, in the order the topologically sorted files
    /// declare them. Deterministic, and used for the offline `grpc list --protoset` path.</summary>
    internal IReadOnlyList<GrpcServiceInfo> DescribeServices() =>
        _built.SelectMany(f => f.Services).Select(Describe).ToList();

    private void AddProto(FileDescriptorProto proto) => _protos.TryAdd(proto.Name, proto);

    /// <summary>Do not assume the server returned the transitive closure of dependencies — server
    /// reflection implementations differ on this. Fetch every dependency filename not yet collected,
    /// and repeat until nothing is missing. Filenames already collected can never be requested again,
    /// which is what keeps a cyclic dependency graph from looping here; the file-count cap below is
    /// the backstop for a server that keeps naming files we have never seen before.
    /// <para>Reflection-only: only ever reached from <see cref="FindServiceAsync"/> after that method
    /// has already confirmed <c>_reflection</c> is not null, so the null-forgiving reference below
    /// does not weaken the protoset mode's "no socket is ever opened" guarantee.</para></summary>
    private async Task ResolveDependenciesAsync(CancellationToken ct)
    {
        while (true)
        {
            var missing = _protos.Values
                .SelectMany(p => p.Dependency)
                .Where(dep => !_protos.ContainsKey(dep))
                .Distinct()
                .ToList();
            if (missing.Count == 0) break;

            foreach (var filename in missing)
            {
                if (_protos.Count >= MaxFilesFetched)
                {
                    // GrpcMethodNotFoundException fits best of the declared exception types:
                    // functionally, this means the service being looked up could not be resolved via
                    // reflection because its dependency graph never stopped growing. This path is
                    // reflection-only (protoset mode never reaches ResolveDependenciesAsync at all),
                    // so it stays a reflection-only message.
                    throw new GrpcMethodNotFoundException(
                        $"Server reflection dependency resolution exceeded {MaxFilesFetched} files while " +
                        $"fetching '{filename}'; the server may be reporting an unbounded dependency graph.");
                }
                var files = await _reflection!.FileByFilenameAsync(filename, ct);
                foreach (var bytes in files) AddProto(FileDescriptorProto.Parser.ParseFrom(bytes));
            }
        }
    }

    /// <summary>Rebuilds every file collected so far (previous calls' files included) into real
    /// descriptors and re-populates the service cache from all of them — not just the one that was
    /// asked for — so a second service living in an already-fetched file costs nothing. In protoset
    /// mode this runs exactly once, at construction, seeding the cache with every service the set
    /// declares; in reflection mode it reruns each time FindServiceAsync fetches something new.</summary>
    private void Rebuild()
    {
        var ordered = TopologicalSort();
        var built = FileDescriptor.BuildFromByteStrings(ordered.Select(p => p.ToByteString()).ToList());

        _built = built;
        _services.Clear();
        _messages.Clear();
        foreach (var file in built)
        {
            foreach (var service in file.Services)
                _services[service.FullName] = service;
            foreach (var message in file.MessageTypes)
                AddMessageRecursive(message);
        }
    }

    /// <summary>Indexes a message and every message nested inside it, however deep. A nested message
    /// (e.g. certapi.test.Outer.Inner) is a perfectly legal Any payload, but it is not in its file's
    /// top-level MessageTypes list at all — only walking NestedTypes recursively finds it.</summary>
    private void AddMessageRecursive(MessageDescriptor message)
    {
        _messages[message.FullName] = message;
        foreach (var nested in message.NestedTypes) AddMessageRecursive(nested);
    }

    /// <summary>Chooses the right exception type for a broken dependency graph: a
    /// GrpcMethodNotFoundException when the descriptors came from a server (something reflection
    /// could still recover from on a retry), a GrpcDescriptorSetException when they came from a
    /// file on disk (nothing left to retry — the file itself is broken).</summary>
    private Exception Broken(string message) =>
        _reflection is null ? new GrpcDescriptorSetException(message) : new GrpcMethodNotFoundException(message);

    /// <summary><see cref="FileDescriptor.BuildFromByteStrings"/> requires each file's dependencies to
    /// precede it in the input; feeding it unsorted input fails in a confusing way. A cycle among the
    /// collected files (which a well-formed compiled `.proto` set could never contain, but a
    /// malicious/buggy server's raw bytes, or a hand-assembled descriptor set, are not obligated to be
    /// well-formed) is reported here rather than looping.</summary>
    private List<FileDescriptorProto> TopologicalSort()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<FileDescriptorProto>();

        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            if (!_protos.TryGetValue(name, out var proto)) return;
            if (!visiting.Add(name))
            {
                throw Broken(
                    $"Circular dependency detected among the file descriptors in {_sourceLabel}, involving '{name}'.");
            }
            foreach (var dependency in proto.Dependency) Visit(dependency);
            visiting.Remove(name);
            visited.Add(name);
            ordered.Add(proto);
        }

        foreach (var name in _protos.Keys) Visit(name);
        return ordered;
    }
}
