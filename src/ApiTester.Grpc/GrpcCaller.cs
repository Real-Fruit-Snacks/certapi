using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using ApiTester.Core;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;

namespace ApiTester.Grpc;

/// <summary>The contained entry point to gRPC support: everything in <see cref="Grpc.Core"/>,
/// <see cref="Grpc.Net.Client"/>, and Google.Protobuf stays behind this class — no type from any of
/// them appears in a public signature here, so a caller (the CLI) never has to reference gRPC's own
/// types.
/// <para>Discovery and invocation report a non-OK status differently, because not every return
/// shape can carry one the same way: <see cref="InvokeAsync"/>, <see cref="InvokeClientStreamingAsync"/>,
/// and <see cref="InvokeDuplexAsync"/> return a non-OK status as data —
/// <see cref="GrpcCallResult"/> has fields for exactly that — while <see cref="DiscoverAsync"/> and
/// <see cref="InvokeStreamingAsync"/> throw <see cref="GrpcStatusException"/>, since neither of those
/// return shapes has anywhere to put a status.</para></summary>
public sealed class GrpcCaller : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ReflectionClient _reflection;
    private readonly DescriptorPool _descriptors;

    // Non-null exactly when the caller supplied a compiled descriptor set at construction. Kept
    // around (rather than only consulted once, to build _descriptors) because DiscoverAsync also
    // needs to know whether to skip reflection's ListServicesAsync entirely — see ruling 2.
    private readonly GrpcDescriptorSet? _protoset;
    private readonly bool _web;

    // A pass-through marshaller: every call's request is already encoded, and its response is
    // decoded by ProtoJsonReader afterwards, so nothing here needs Grpc.Core to know about Protobuf
    // at all. Request and response share the same byte[]-to-byte[] shape, so one marshaller instance
    // serves both positions Method<TRequest, TResponse> requires, for every call this class makes.
    private static readonly Marshaller<byte[]> ByteMarshaller = Marshallers.Create<byte[]>(b => b, b => b);

    /// <param name="trustServerCertificate">Consulted when the server's certificate fails ordinary
    /// validation, so a host pinned with `certapi trust add` is reachable without --insecure — the
    /// same seam ApiClient already uses.</param>
    /// <param name="descriptors">When supplied, this compiled descriptor set is the sole source of
    /// descriptors for this caller: server reflection is never consulted, not even to fill in
    /// something the set happens to be missing. Explicit input beats discovery (ruling 2) — the two
    /// are never merged — which is also what makes a server that does not implement reflection at
    /// all callable, as long as its contracts were compiled with
    /// `protoc --descriptor_set_out=&lt;file&gt; --include_imports`.</param>
    public GrpcCaller(Uri address, X509Certificate2? clientCertificate, TransportOptions transport,
                       Func<X509Certificate2?, bool>? trustServerCertificate = null,
                       GrpcDescriptorSet? descriptors = null)
    {
        // Eager: a bad address (wrong scheme, unreachable channel construction) should fail fast,
        // before the caller has invested in a discovery or invoke call.
        _channel = GrpcChannelFactory.Create(address, clientCertificate, transport, trustServerCertificate);
        _web = transport.GrpcWire != GrpcWire.Native;
        _reflection = new ReflectionClient(_channel);
        _protoset = descriptors;
        _descriptors = descriptors is null ? new DescriptorPool(_reflection) : descriptors.Pool;
    }

    /// <summary>When a descriptor set was supplied at construction, exactly the services it declares
    /// — nothing more, nothing less, and never merged with what a server would have advertised.
    /// Otherwise, the services the server advertises via reflection, in the order it listed them, each
    /// with its methods. If one service's descriptors cannot be resolved, that service is returned
    /// with an empty method list rather than failing the whole listing — one uncooperative service
    /// should not hide the rest, and an empty list is visible instead of silent. Throws
    /// <see cref="GrpcReflectionUnavailableException"/> if the server does not implement reflection at
    /// all, or <see cref="GrpcStatusException"/> for any other failure listing services.</summary>
    public async Task<IReadOnlyList<GrpcServiceInfo>> DiscoverAsync(CancellationToken ct)
    {
        // Ruling 2 in force: a descriptor set is an explicit statement of what the caller wants to
        // call, so it wins over whatever the server would have advertised, and the two are never
        // merged — not even to fill in something the set happens to lack. No reflection round trip
        // happens here in protoset mode; a bare await never runs, so this returns synchronously in
        // practice despite the async signature. One visible consequence, pinned by
        // GrpcProtosetCallerTests: a reflection-enabled server also advertises
        // grpc.reflection.v1alpha.ServerReflection (the reflection service) as one of its own
        // services, but a descriptor-set listing never includes it, because the set itself does not
        // declare it.
        if (_protoset is not null) return _protoset.Services;

        var names = await _reflection.ListServicesAsync(ct);
        var loaded = await _descriptors.LoadServicesAsync(names, ct);
        var services = new List<GrpcServiceInfo>(names.Count);
        foreach (var name in names)
        {
            IReadOnlyList<GrpcMethodInfo> methods = loaded.TryGetValue(name, out var service)
                ? DescriptorPool.Describe(service).Methods
                : Array.Empty<GrpcMethodInfo>();
            services.Add(new GrpcServiceInfo(name, methods));
        }
        return services;
    }

    /// <summary>Makes one unary call. Request JSON that does not match the discovered input type
    /// throws <see cref="GrpcJsonException"/> naming the offending field. A metadata key ending in
    /// "-bin" needs a binary value, which a string pair cannot supply, so that throws
    /// <see cref="ArgumentException"/> naming the key — the command layer turns that into a usage
    /// error, since it is a caller mistake rather than a call failure.</summary>
    public async Task<GrpcCallResult> InvokeAsync(string service, string method, string requestJson,
        IReadOnlyList<KeyValuePair<string, string>> metadata, CancellationToken ct)
    {
        var methodDescriptor = await ResolveMethodAsync(service, method, ct);
        RequireKind(methodDescriptor, service, method, wantClientStreaming: false, wantServerStreaming: false);

        byte[] requestBytes = ProtoJsonWriter.ToProtobuf(methodDescriptor.InputType, requestJson, _descriptors.FindMessage);
        var callMetadata = BuildMetadata(metadata);

        var grpcMethod = new Method<byte[], byte[]>(MethodType.Unary, service, method, ByteMarshaller, ByteMarshaller);

        var stopwatch = Stopwatch.StartNew();
        using var call = _channel.CreateCallInvoker().AsyncUnaryCall(
            grpcMethod, host: null, new CallOptions(callMetadata, cancellationToken: ct), requestBytes);
        try
        {
            byte[] responseBytes = await call.ResponseAsync;
            var elapsed = stopwatch.Elapsed;
            string responseJson = ProtoJsonReader.ToJson(methodDescriptor.OutputType, responseBytes, indented: true, _descriptors.FindMessage);
            return new GrpcCallResult(0, "OK", "", responseJson, GrpcFailure.ToPairs(call.GetTrailers()), elapsed);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            // A user who pressed Ctrl+C did not get an answer from the server; do not hand back a
            // GrpcCallResult that looks like one.
            throw new OperationCanceledException(ct);
        }
        catch (RpcException ex)
        {
            var elapsed = stopwatch.Elapsed;
            var failure = GrpcFailure.FromRpcException(ex);
            return new GrpcCallResult(failure.StatusCode, failure.StatusName, failure.StatusDetail, "", failure.Trailers, elapsed);
        }
    }

    /// <summary>Invokes a server-streaming method, yielding each message as compact single-line JSON
    /// (<c>indented: false</c>) — deliberately unlike <see cref="InvokeAsync"/>'s indented output: a
    /// stream of pretty-printed objects is neither readable nor pipeable, whereas one compact object
    /// per line is both.
    /// <para>A non-OK status that ends the stream throws <see cref="GrpcStatusException"/>, carrying
    /// the code, name, detail, and trailers — after every message that did arrive has already been
    /// yielded, so a caller can see the partial stream and still learn why it stopped.
    /// <see cref="IAsyncEnumerable{T}"/> has nowhere else to put a status, which is why this path
    /// throws where <see cref="InvokeAsync"/> returns one as data. For the same reason, a
    /// successful stream's trailers and total elapsed time are not returned here: the
    /// <c>IAsyncEnumerable&lt;string&gt;</c> return shape cannot carry them, so a caller that needs
    /// timing measures it itself.</para>
    /// <para>A consumer that stops enumerating early (a <c>break</c> out of <c>await foreach</c>)
    /// disposes and cancels the underlying call rather than leaking a half-open stream — the
    /// mechanism the command layer's <c>--max-messages</c> relies on. Request JSON that does not
    /// match the discovered input type throws <see cref="GrpcJsonException"/>; a client-streaming or
    /// bidirectional method throws <see cref="GrpcUnsupportedMethodException"/>, as does a unary
    /// method (use <see cref="InvokeAsync"/> instead). Because this is an async iterator, none of
    /// that runs until the caller actually begins enumerating — not at the point this method is
    /// called.</para></summary>
    public async IAsyncEnumerable<string> InvokeStreamingAsync(
        string service, string method, string requestJson,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var methodDescriptor = await ResolveMethodAsync(service, method, ct);
        RequireKind(methodDescriptor, service, method, wantClientStreaming: false, wantServerStreaming: true);

        byte[] requestBytes = ProtoJsonWriter.ToProtobuf(methodDescriptor.InputType, requestJson, _descriptors.FindMessage);
        var callMetadata = BuildMetadata(metadata);

        var grpcMethod = new Method<byte[], byte[]>(MethodType.ServerStreaming, service, method, ByteMarshaller, ByteMarshaller);

        using var call = _channel.CreateCallInvoker().AsyncServerStreamingCall(
            grpcMethod, host: null, new CallOptions(callMetadata, cancellationToken: ct), requestBytes);

        while (true)
        {
            bool moved;
            try
            {
                moved = await call.ResponseStream.MoveNext(ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
            {
                // A user who pressed Ctrl+C did not get an answer from the server.
                throw new OperationCanceledException(ct);
            }
            catch (RpcException ex)
            {
                throw GrpcFailure.FromRpcException(ex);
            }
            if (!moved) break;
            yield return ProtoJsonReader.ToJson(methodDescriptor.OutputType, call.ResponseStream.Current, indented: false, _descriptors.FindMessage);
        }
    }

    /// <summary>Invokes a client-streaming method: every message in <paramref name="requestMessages"/>
    /// is sent, in order, before the server's single reply is awaited. A non-OK status comes back as
    /// data — a <see cref="GrpcCallResult"/>, exactly the way <see cref="InvokeAsync"/> reports one —
    /// never thrown, because this return shape (unlike <see cref="InvokeStreamingAsync"/>'s) has
    /// somewhere to put it. An empty <paramref name="requestMessages"/> sequence is legal: nothing is
    /// sent, the request stream is completed immediately, and the server's reply (or failure) is
    /// awaited as usual — a client-streaming method with no messages at all is a valid, empty stream,
    /// not a usage error. The single reply renders indented, like <see cref="InvokeAsync"/>'s: exactly
    /// one response message is not a stream to render compactly.
    /// <para>Malformed JSON in any message throws <see cref="GrpcJsonException"/> naming the offending
    /// field, exactly as <see cref="InvokeAsync"/>'s request encoding does — encoding happens before
    /// the write, so a bad message is never silently absorbed by the write tolerance described below.
    /// A unary, server-streaming, or bidirectional method throws
    /// <see cref="GrpcUnsupportedMethodException"/>.</para>
    /// <para>A write (or the final <c>CompleteAsync</c>) can lose a race against a server that has
    /// already ended the call — the test fixture's <c>ClientStreamThenFail</c> does exactly this,
    /// failing as soon as its first message arrives. Grpc.Core surfaces that race as an
    /// <see cref="RpcException"/> or <see cref="InvalidOperationException"/> thrown from the write
    /// itself, but the authoritative status always lives on <c>ResponseAsync</c>, so a write that
    /// loses this race is deliberately absorbed rather than reported: sending simply stops, and the
    /// status the caller sees is the one the response carries, not whatever the losing write
    /// threw.</para></summary>
    public async Task<GrpcCallResult> InvokeClientStreamingAsync(
        string service, string method,
        IAsyncEnumerable<string> requestMessages,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        CancellationToken ct)
    {
        var methodDescriptor = await ResolveMethodAsync(service, method, ct);
        RequireKind(methodDescriptor, service, method, wantClientStreaming: true, wantServerStreaming: false);
        if (_web)
            throw new GrpcUnsupportedMethodException($"'{service}/{method}' streams requests, which gRPC-Web cannot carry (it is unary and server-streaming only). Drop --grpc-web to call it over native gRPC.");

        var callMetadata = BuildMetadata(metadata);
        var grpcMethod = new Method<byte[], byte[]>(MethodType.ClientStreaming, service, method, ByteMarshaller, ByteMarshaller);

        var stopwatch = Stopwatch.StartNew();
        using var call = _channel.CreateCallInvoker().AsyncClientStreamingCall(
            grpcMethod, host: null, new CallOptions(callMetadata, cancellationToken: ct));

        await foreach (var json in requestMessages.WithCancellation(ct))
        {
            // Encoding happens outside TryWriteAsync's tolerance: a malformed message is the caller's
            // mistake, never a race against the server, and must propagate as GrpcJsonException.
            byte[] requestBytes = ProtoJsonWriter.ToProtobuf(methodDescriptor.InputType, json, _descriptors.FindMessage);
            if (!await TryWriteAsync(call.RequestStream, requestBytes)) break;
        }
        await TryCompleteAsync(call.RequestStream);

        try
        {
            byte[] responseBytes = await call.ResponseAsync;
            var elapsed = stopwatch.Elapsed;
            string responseJson = ProtoJsonReader.ToJson(methodDescriptor.OutputType, responseBytes, indented: true, _descriptors.FindMessage);
            return new GrpcCallResult(0, "OK", "", responseJson, GrpcFailure.ToPairs(call.GetTrailers()), elapsed);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
        {
            // A user who pressed Ctrl+C did not get an answer from the server; do not hand back a
            // GrpcCallResult that looks like one.
            throw new OperationCanceledException(ct);
        }
        catch (RpcException ex)
        {
            var elapsed = stopwatch.Elapsed;
            var failure = GrpcFailure.FromRpcException(ex);
            return new GrpcCallResult(failure.StatusCode, failure.StatusName, failure.StatusDetail, "", failure.Trailers, elapsed);
        }
    }

    /// <summary>Invokes a bidirectional-streaming method: <paramref name="requestMessages"/> is sent
    /// and the server's responses are received CONCURRENTLY, on independent paths — sending the next
    /// message never waits for a response to arrive, and delivering a response never waits for the
    /// next message to be sent. That is the entire reason bidirectional streaming exists: a method
    /// like the test fixture's BidiStream, which replies to each request as it arrives, would
    /// otherwise deadlock (or silently degrade into "send everything, then read everything") if this
    /// method sent every message to completion before it started reading any response.
    /// <para>Each response is handed to <paramref name="onResponse"/>, in arrival order, as compact
    /// single-line JSON (<c>indented: false</c>) — the same rendering <see cref="InvokeStreamingAsync"/>
    /// yields, for the same reason: a stream of pretty-printed objects is neither readable nor
    /// pipeable, whereas one compact object per line is both. Returning <c>true</c> keeps the call
    /// open for the next response; returning <c>false</c> stops it early — the mechanism the command
    /// layer's <c>--max-messages</c> relies on. Stopping early is reported as success (status code 0),
    /// not failure: the consumer that chose to stop is the one that reports it, the same convention
    /// <see cref="InvokeStreamingAsync"/> already has for its own early-stop path. Trailers are not
    /// available on that path — the call was torn down rather than allowed to finish — so they come
    /// back empty rather than being read after teardown, which is not guaranteed to work.</para>
    /// <para>An empty <paramref name="requestMessages"/> sequence is legal: nothing is sent, the
    /// request stream is completed immediately, and only the response side is observed — a
    /// bidirectional method with no messages at all is a valid, empty stream, not a usage error.
    /// <c>ResponseJson</c> is always empty on this path, whether the call ends in OK or not: every
    /// response message that does arrive goes to <paramref name="onResponse"/>, not into the result,
    /// so there is nowhere else for it to go.</para>
    /// <para>A non-OK status comes back as data — a <see cref="GrpcCallResult"/>, never thrown —
    /// AFTER <paramref name="onResponse"/> has already been given every message that did arrive, the
    /// same convention <see cref="InvokeClientStreamingAsync"/> uses for its own single reply. A
    /// unary, server-streaming, or client-streaming method throws
    /// <see cref="GrpcUnsupportedMethodException"/>, naming the method's actual kind and the entry
    /// point that does handle it.</para>
    /// <para>Malformed JSON in any message throws <see cref="GrpcJsonException"/> naming the
    /// offending field, exactly as every other entry point's request encoding does. Because the
    /// receive loop can be parked on a server that is waiting for more input when that happens, the
    /// send side cancels the call's own linked token (never the caller's own <paramref name="ct"/>)
    /// so the receive loop unwinds instead of hanging, and only once both sides have stopped is the
    /// encoding failure rethrown — with its original stack trace intact — rather than being reported
    /// as whatever status that teardown happened to produce.</para></summary>
    public async Task<GrpcCallResult> InvokeDuplexAsync(
        string service, string method,
        IAsyncEnumerable<string> requestMessages,
        IReadOnlyList<KeyValuePair<string, string>> metadata,
        Func<string, bool> onResponse,
        CancellationToken ct)
    {
        var methodDescriptor = await ResolveMethodAsync(service, method, ct);
        RequireKind(methodDescriptor, service, method, wantClientStreaming: true, wantServerStreaming: true);
        if (_web)
            throw new GrpcUnsupportedMethodException($"'{service}/{method}' streams requests, which gRPC-Web cannot carry (it is unary and server-streaming only). Drop --grpc-web to call it over native gRPC.");

        var callMetadata = BuildMetadata(metadata);
        var grpcMethod = new Method<byte[], byte[]>(MethodType.DuplexStreaming, service, method, ByteMarshaller, ByteMarshaller);

        var stopwatch = Stopwatch.StartNew();
        // Call-scoped, not the caller's own token: this lets the send side be torn down the instant
        // the consumer stops (onResponse returns false) or the send side itself fails, without ever
        // cancelling ct — the caller's own cancellation is still reported honestly below.
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var call = _channel.CreateCallInvoker().AsyncDuplexStreamingCall(
            grpcMethod, host: null, new CallOptions(callMetadata, cancellationToken: callCts.Token));

        Exception? sendFailure = null;

        async Task SendAsync()
        {
            try
            {
                await foreach (var json in requestMessages.WithCancellation(callCts.Token))
                {
                    // Encoding happens outside TryWriteAsync's tolerance: a malformed message is the
                    // caller's mistake, never a race against the server, and must propagate as
                    // GrpcJsonException.
                    byte[] bytes = ProtoJsonWriter.ToProtobuf(methodDescriptor.InputType, json, _descriptors.FindMessage);
                    if (!await TryWriteAsync(call.RequestStream, bytes)) return;
                }
                await TryCompleteAsync(call.RequestStream);
            }
            catch (OperationCanceledException)
            {
                // The receive side tore the call down (the consumer stopped, or the receive side
                // itself hit a non-OK status); that is not a failure of the send side's own.
            }
            catch (Exception ex)
            {
                // A malformed message (GrpcJsonException) is the caller's error and must reach them,
                // but the receive loop may be parked on a server that is waiting for more input, so
                // cancelling the call is what lets that error be reported instead of hanging.
                sendFailure = ex;
                callCts.Cancel();
            }
        }

        // Started, and deliberately not awaited yet: awaiting it here would send every message to
        // completion before a single response is read, which is exactly the "send everything, then
        // read everything" shape a bidirectional call must not have.
        var sendTask = SendAsync();

        GrpcCallResult result;
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await call.ResponseStream.MoveNext(callCts.Token);
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && ct.IsCancellationRequested)
                {
                    // A user who pressed Ctrl+C did not get an answer from the server; do not hand
                    // back a GrpcCallResult that looks like one.
                    throw new OperationCanceledException(ct);
                }
                catch (RpcException ex)
                {
                    // Every message that did arrive has already reached onResponse by this point;
                    // the status is data, never thrown, for the same reason InvokeClientStreamingAsync
                    // does not throw for its own single reply.
                    var elapsed = stopwatch.Elapsed;
                    var failure = GrpcFailure.FromRpcException(ex);
                    result = new GrpcCallResult(failure.StatusCode, failure.StatusName, failure.StatusDetail, "", failure.Trailers, elapsed);
                    break;
                }

                if (!moved)
                {
                    // The stream ended normally: trailers are read now, before anything below is
                    // cancelled, because they are only available once the call has genuinely
                    // finished — reading them after teardown is not guaranteed to work.
                    var elapsed = stopwatch.Elapsed;
                    result = new GrpcCallResult(0, "OK", "", "", GrpcFailure.ToPairs(call.GetTrailers()), elapsed);
                    break;
                }

                string responseJson = ProtoJsonReader.ToJson(methodDescriptor.OutputType, call.ResponseStream.Current, indented: false, _descriptors.FindMessage);
                if (!onResponse(responseJson))
                {
                    // The consumer asked to stop; that is success, not failure, and trailers are not
                    // available on this path because the call is being torn down rather than allowed
                    // to finish.
                    var elapsed = stopwatch.Elapsed;
                    result = new GrpcCallResult(0, "OK", "", "", Array.Empty<KeyValuePair<string, string>>(), elapsed);
                    break;
                }
            }
        }
        finally
        {
            // Runs on every exit path, including the OperationCanceledException thrown above: tear
            // the call down and join the send side before this method's `using var call` /
            // `using var callCts` are disposed, so no work outlives them.
            callCts.Cancel();
            await sendTask;
        }

        // SendAsync records rather than throws, so a malformed message must be surfaced here — after
        // teardown, preserving its original stack — rather than being masked by whatever Cancelled
        // status tearing the call down produced.
        if (sendFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sendFailure).Throw();
        }
        return result;
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Builds call metadata from a caller-supplied string pair list, shared by every call
    /// this class makes. A metadata key ending in "-bin" needs a binary value, which a string pair
    /// cannot supply, so that throws <see cref="ArgumentException"/> naming the key — the command
    /// layer turns that into a usage error, since it is a caller mistake rather than a call
    /// failure.</summary>
    private static Metadata BuildMetadata(IReadOnlyList<KeyValuePair<string, string>> metadata)
    {
        var callMetadata = new Metadata();
        foreach (var pair in metadata)
        {
            if (pair.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Metadata key '{pair.Key}' ends with '-bin' and needs a binary value; a string metadata pair cannot supply one.",
                    nameof(metadata));
            }
            callMetadata.Add(pair.Key, pair.Value);
        }
        return callMetadata;
    }

    /// <summary>Resolves a service and method exactly as every call path needs to: the same
    /// <see cref="DescriptorPool.FindServiceAsync"/> lookup, and the same
    /// <see cref="GrpcMethodNotFoundException"/> message naming the methods that do exist, shared so
    /// every entry point reports an unknown method identically.</summary>
    private async Task<MethodDescriptor> ResolveMethodAsync(string service, string method, CancellationToken ct)
    {
        var serviceDescriptor = await _descriptors.FindServiceAsync(service, ct);
        var methodDescriptor = serviceDescriptor.Methods.FirstOrDefault(m => m.Name == method);
        if (methodDescriptor is null)
        {
            string known = serviceDescriptor.Methods.Count == 0
                ? "(none)"
                : string.Join(", ", serviceDescriptor.Methods.Select(m => m.Name));
            throw new GrpcMethodNotFoundException(
                $"Method '{method}' was not found on service '{service}'. Methods that do exist: {known}.");
        }
        return methodDescriptor;
    }

    /// <summary>What each of the four method kinds is called, and which entry point on this class
    /// handles it — the vocabulary every "wrong entry point for this method" message below is built
    /// from.</summary>
    private static (string Kind, string EntryPoint) DescribeKind(bool isClientStreaming, bool isServerStreaming) =>
        (isClientStreaming, isServerStreaming) switch
        {
            (false, false) => ("unary", nameof(InvokeAsync)),
            (false, true) => ("server-streaming", nameof(InvokeStreamingAsync)),
            (true, false) => ("client-streaming", nameof(InvokeClientStreamingAsync)),
            (true, true) => ("bidirectional", nameof(InvokeDuplexAsync)),
        };

    /// <summary>Throws <see cref="GrpcUnsupportedMethodException"/> naming the method's actual kind
    /// and the entry point that does handle it, unless the method's actual streaming shape already
    /// matches what the calling entry point wants — the one guard every <c>InvokeXxxAsync</c> method
    /// applies to reject the other three kinds.</summary>
    private static void RequireKind(MethodDescriptor methodDescriptor, string service, string method,
        bool wantClientStreaming, bool wantServerStreaming)
    {
        if (methodDescriptor.IsClientStreaming == wantClientStreaming
            && methodDescriptor.IsServerStreaming == wantServerStreaming)
        {
            return;
        }
        var (kind, entryPoint) = DescribeKind(methodDescriptor.IsClientStreaming, methodDescriptor.IsServerStreaming);
        throw new GrpcUnsupportedMethodException($"'{service}/{method}' is {kind}; use {entryPoint} instead.");
    }

    /// <summary>Writes one already-encoded message, reporting rather than throwing when the server
    /// has already ended the call: the authoritative status lives on the response, and a write that
    /// loses that race must not become the error the user sees.</summary>
    private static async Task<bool> TryWriteAsync(IClientStreamWriter<byte[]> stream, byte[] bytes)
    {
        try
        {
            await stream.WriteAsync(bytes);
            return true;
        }
        catch (RpcException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Completes a request stream, with the same tolerance <see cref="TryWriteAsync"/> gives
    /// a write: a server that has already ended the call can make completion itself race-lose the
    /// same way, and that must not surface as the error either — the response still carries the real
    /// status.</summary>
    private static async Task TryCompleteAsync(IClientStreamWriter<byte[]> stream)
    {
        try
        {
            await stream.CompleteAsync();
        }
        catch (RpcException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
