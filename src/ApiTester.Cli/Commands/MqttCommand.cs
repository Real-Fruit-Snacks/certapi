using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApiTester.Core;

namespace ApiTester.Cli.Commands;

public static class MqttCommand
{
    public const string Help = """
        Usage: certapi mqtt <broker-url> [options]

        A small MQTT 3.1.1 / 5.0 client that presents the client certificate: subscribe and print
        messages, publish messages, or both. The broker URL picks the wire:
          mqtts://host[:8883]       MQTT over TLS (the usual mutual-TLS setup: AWS IoT, Azure IoT
                                    Hub / Event Grid, HiveMQ, EMQX, Mosquitto)
          wss://host[:443]/path     MQTT over a secure WebSocket (subprotocol "mqtt"; the path
                                    defaults to /mqtt)
          mqtt://host[:1883]        Plain TCP, and ws://host/path, for a test broker

        Subscribe:
          -t, --topic <filter>      Subscribe to a topic filter (repeatable; + and # wildcards)
          --count <n>               Stop after n messages
          --seconds <n>             Stop after n seconds
          --json                    Print each message as {"topic","payload","qos","retain"}
                                    (payloadBase64 instead of payload when it is not UTF-8 text)

        Publish:
          --publish <topic>         Publish to this topic: each -m value, or each line on stdin
          -m, --message <text>      A message to publish (repeatable; @file reads a file)
          --retain                  Ask the broker to retain the published messages

        Session:
          --qos <0|1>               QoS for publishing and subscribing (default 0; QoS 1 waits for
                                    the broker's acknowledgement)
          --client-id <id>          Default: certapi- and a random suffix (AWS IoT policies often
                                    require a specific one)
          --username <name>         MQTT user name (Azure IoT Hub: {hub}.azure-devices.net/{device}/?api-version=2021-04-12)
          --password <secret>       MQTT password (or set CERTAPI_MQTT_PASSWORD)
          --mqtt-version <v>        3.1.1 (default) or 5
          --keepalive <seconds>     Default 60
          --alpn <protocol>         TLS ALPN for mqtts:// (AWS IoT on port 443 needs x-amzn-mqtt-ca)
          -q, --quiet               No connect/subscribe notices on stderr

        TLS / certificates:
        """ + "\n" + CliCert.HelpLines + "\n" + """
          --insecure                Ignore server certificate errors

        """ + "\n" + TransportFlags.StreamHelp + "\n" + """

        Examples:
          certapi mqtt mqtts://a1b2c3-ats.iot.eu-west-1.amazonaws.com --cert-file device.pfx --client-id sensor-7 -t 'sensors/#' --count 5
          certapi mqtt mqtts://broker.example.com --cert "CN=Device 7" --publish sensors/7/temp -m '{"c":21.5}' --qos 1
          certapi mqtt wss://broker.example.com/mqtt --cert-file device.pfx -t alerts --seconds 60 --json

        Messages go to stdout as "topic payload"; notices go to stderr. Exit 0 on success, 1 when the
        broker refuses the connection, a subscription, or a publish.
        """;

    public static int Run(Args args, TextReader input, TextWriter stdout, TextWriter stderr, CliServices services)
    {
        var topics = args.Values("-t", "--topic");
        string? publishTopic = args.Value("--publish");
        var messages = args.Values("-m", "--message");
        bool retain = args.Flag("--retain");
        string? qosRaw = args.Value("--qos");
        string clientId = args.Value("--client-id") ?? "certapi-" + Guid.NewGuid().ToString("N")[..8];
        string? username = args.Value("--username");
        string? password = args.Value("--password") ?? Environment.GetEnvironmentVariable("CERTAPI_MQTT_PASSWORD");
        string versionRaw = args.Value("--mqtt-version") ?? "3.1.1";
        string? keepAliveRaw = args.Value("--keepalive");
        var alpn = args.Values("--alpn");
        bool json = args.Flag("--json");
        bool quiet = args.Flag("-q", "--quiet");
        string store = args.Value("--store") ?? services.Profile?.Store ?? "CurrentUser";
        bool insecure = args.Switch("--insecure", services.Profile?.Insecure);
        string? workspace = args.Value("--workspace");
        int? count = PositiveInt(args.Value("--count"), "--count");
        int? seconds = PositiveInt(args.Value("--seconds"), "--seconds");
        var transport = TransportFlags.ParseStreamSubset(args, insecure, environment: null, services.Profile);
        var cert = CliCert.Resolve(args, store, services, stderr);

        var positionals = args.Positionals();
        if (positionals.Count != 1) throw new CliUsageException(Help);
        if (!Uri.TryCreate(positionals[0], UriKind.Absolute, out var broker) ||
            broker.Scheme is not ("mqtt" or "mqtts" or "ws" or "wss" or "tcp" or "ssl"))
            throw new CliUsageException($"mqtt needs an mqtts://, wss://, mqtt:// or ws:// broker URL, got '{positionals[0]}'.");

        int version = versionRaw switch
        {
            "3.1.1" or "3" or "4" or "311" => 4,
            "5" or "5.0" => 5,
            _ => throw new CliUsageException($"--mqtt-version expects 3.1.1 or 5, got '{versionRaw}'.")
        };
        int qos = qosRaw switch
        {
            null or "0" => 0,
            "1" => 1,
            "2" => throw new CliUsageException("--qos 2 is not supported; use 0 or 1."),
            _ => throw new CliUsageException($"--qos expects 0 or 1, got '{qosRaw}'.")
        };
        int keepAlive = 60;
        if (keepAliveRaw is not null && (!int.TryParse(keepAliveRaw, out keepAlive) || keepAlive < 0 || keepAlive > 65535))
            throw new CliUsageException($"--keepalive expects 0–65535 seconds, got '{keepAliveRaw}'.");
        if (topics.Count == 0 && publishTopic is null)
            throw new CliUsageException("mqtt needs --topic to subscribe, --publish to publish, or both.");
        if (messages.Count > 0 && publishTopic is null)
            throw new CliUsageException("-m/--message needs --publish <topic>.");
        if (retain && publishTopic is null)
            throw new CliUsageException("--retain needs --publish <topic>.");
        if (publishTopic is not null && (publishTopic.Contains('+') || publishTopic.Contains('#')))
            throw new CliUsageException("--publish names a topic, not a filter: + and # are not allowed in it.");
        if (alpn.Count > 0 && broker.Scheme is not ("mqtts" or "ssl"))
            throw new CliUsageException("--alpn applies to mqtts:// only.");

        var payloads = new List<byte[]>();
        foreach (var m in messages)
            payloads.Add(m.StartsWith('@') ? File.ReadAllBytes(m[1..]) : Encoding.UTF8.GetBytes(m));
        bool interactive = ReferenceEquals(input, Console.In) && !Console.IsInputRedirected;
        if (publishTopic is not null && messages.Count == 0 && !interactive)
        {
            string? line;
            while ((line = input.ReadLine()) is not null) payloads.Add(Encoding.UTF8.GetBytes(line));
        }
        if (publishTopic is not null && payloads.Count == 0)
            throw new CliUsageException("--publish has nothing to send: give -m or pipe lines on stdin.");

        var state = CliWorkspace.Load(workspace, services.LiveStatePath, stderr);
        var predicates = new TrustPredicates(state);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(services.Cancel);
        if (seconds is { } s) cts.CancelAfter(TimeSpan.FromSeconds(s));

        return RunAsync().GetAwaiter().GetResult();

        async Task<int> RunAsync()
        {
            Stream stream;
            WebSocketSession? session = null;
            try
            {
                if (!quiet) stderr.WriteLine($"connecting to {broker.GetLeftPart(UriPartial.Path)} …");
                stream = await OpenAsync();
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                          or System.Security.Authentication.AuthenticationException
                                          or System.Net.WebSockets.WebSocketException or System.Net.Http.HttpRequestException
                                          or InvalidOperationException or OperationCanceledException)
            {
                stderr.WriteLine("error: " + ex.Message);
                return ExitCodes.Failure;
            }

            await using var streamScope = stream;
            await using var sessionScope = session;
            var writeLock = new SemaphoreSlim(1, 1);
            async Task WriteAsync(byte[] packet)
            {
                await writeLock.WaitAsync(CancellationToken.None);
                try
                {
                    await stream.WriteAsync(packet, CancellationToken.None);
                    await stream.FlushAsync(CancellationToken.None);
                }
                finally { writeLock.Release(); }
            }

            int received = 0;
            bool failed = false;
            var watch = Stopwatch.StartNew();
            ushort nextId = 0;
            ushort NextId() => nextId = (ushort)(nextId == ushort.MaxValue ? 1 : nextId + 1);

            async Task<MqttPacket?> NextAsync() => await MqttCodec.ReadAsync(stream, cts.Token);

            async Task OnPublishAsync(MqttPacket p)
            {
                var message = MqttCodec.DecodePublish(version, p, out var id);
                if (message.Qos == 1) await WriteAsync(MqttCodec.EncodePubAck(id));
                received++;
                stdout.WriteLine(json ? Json(message) : message.Topic + " " + Text(message.Payload));
                stdout.Flush();
            }

            // Wait for one answer, printing any messages that arrive first (retained messages often
            // arrive before the SUBACK has been read).
            async Task<MqttPacket?> WaitForAsync(int type, ushort id)
            {
                while (await NextAsync() is { } p)
                {
                    if (p.Type == MqttCodec.Publish) { await OnPublishAsync(p); continue; }
                    if (p.Type == type && (id == 0 || MqttCodec.PacketIdOf(p) == id)) return p;
                    if (p.Type == MqttCodec.Disconnect) return p;
                }
                return null;
            }

            try
            {
                await WriteAsync(MqttCodec.EncodeConnect(version, clientId, username, password, keepAlive, cleanStart: true));
                var connAck = await WaitForAsync(MqttCodec.ConnAck, 0);
                if (connAck is null || connAck.Type != MqttCodec.ConnAck)
                {
                    stderr.WriteLine("error: the broker closed the connection before accepting it" +
                        (cert is null ? " (does it require a client certificate?)" : ""));
                    return ExitCodes.Failure;
                }
                int code = MqttCodec.ConnAckCode(connAck);
                if (code != 0)
                {
                    stderr.WriteLine($"error: the broker refused the connection: {MqttCodec.ConnAckReason(version, code)}");
                    return ExitCodes.Failure;
                }
                if (!quiet) stderr.WriteLine($"connected as {clientId} (MQTT {(version == 5 ? "5.0" : "3.1.1")}).");

                if (keepAlive > 0)
                    _ = Task.Run(async () =>
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, keepAlive * 3 / 4)));
                        try
                        {
                            while (await timer.WaitForNextTickAsync(cts.Token))
                                await WriteAsync(MqttCodec.PingRequest);
                        }
                        catch { /* stopped */ }
                    });

                if (topics.Count > 0)
                {
                    ushort id = NextId();
                    await WriteAsync(MqttCodec.EncodeSubscribe(version, id, topics.Select(t => (t, qos))));
                    var subAck = await WaitForAsync(MqttCodec.SubAck, id);
                    if (subAck?.Type != MqttCodec.SubAck)
                    {
                        stderr.WriteLine("error: the broker closed the connection instead of answering the subscription");
                        return ExitCodes.Failure;
                    }
                    var codes = MqttCodec.SubAckCodes(version, subAck);
                    for (int i = 0; i < topics.Count; i++)
                    {
                        int granted = i < codes.Count ? codes[i] : 0x80;
                        if (granted >= 0x80)
                        {
                            failed = true;
                            stderr.WriteLine($"error: the broker refused the subscription to {topics[i]} (code 0x{granted:X2})");
                        }
                        else if (!quiet) stderr.WriteLine($"subscribed to {topics[i]} (QoS {granted}).");
                    }
                    if (failed && codes.All(c => c >= 0x80))
                    {
                        await WriteAsync(MqttCodec.DisconnectPacket);
                        return ExitCodes.Failure;
                    }
                }

                if (publishTopic is not null)
                    foreach (var payload in payloads)
                    {
                        ushort id = qos > 0 ? NextId() : (ushort)0;
                        await WriteAsync(MqttCodec.EncodePublish(version, publishTopic, payload, qos, retain, id));
                        if (qos > 0)
                        {
                            var ack = await WaitForAsync(MqttCodec.PubAck, id);
                            if (ack?.Type != MqttCodec.PubAck)
                            {
                                stderr.WriteLine("error: the broker closed the connection instead of acknowledging the publish");
                                return ExitCodes.Failure;
                            }
                            // MQTT 5 puts a reason code after the packet id; 0x80 and above is a refusal.
                            if (version == 5 && ack.Body.Length >= 3 && ack.Body[2] >= 0x80)
                            {
                                failed = true;
                                stderr.WriteLine($"error: the broker refused the publish to {publishTopic} (code 0x{ack.Body[2]:X2})");
                                continue;
                            }
                        }
                        if (!quiet) stderr.WriteLine($"> {publishTopic} ({payload.Length} bytes{(qos > 0 ? ", acknowledged" : "")})");
                    }

                if (topics.Count > 0)
                {
                    while ((count is null || received < count) && await NextAsync() is { } p)
                    {
                        if (p.Type == MqttCodec.Publish) await OnPublishAsync(p);
                        else if (p.Type == MqttCodec.Disconnect)
                        {
                            if (!quiet) stderr.WriteLine("the broker disconnected.");
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                if (!quiet && services.Cancel.IsCancellationRequested) stderr.WriteLine("cancelled.");
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
                                          or System.Net.WebSockets.WebSocketException)
            {
                stderr.WriteLine("error: the connection broke: " + ex.Message);
                return ExitCodes.Failure;
            }

            try { await WriteAsync(MqttCodec.DisconnectPacket); } catch { /* already gone */ }
            if (session is not null) await session.CloseAsync(CancellationToken.None);
            if (!quiet && topics.Count > 0)
                stderr.WriteLine($"done after {watch.Elapsed.TotalSeconds:F1} s ({received} message{(received == 1 ? "" : "s")}).");
            return failed ? ExitCodes.Failure : ExitCodes.Ok;

            async Task<Stream> OpenAsync()
            {
                switch (broker.Scheme)
                {
                    case "mqtts" or "ssl":
                        return await StreamTransport.OpenTlsAsync(broker.Host, broker.IsDefaultPort || broker.Port < 0 ? 8883 : broker.Port,
                            cert, transport, predicates.For(broker.Host), alpn.Count > 0 ? alpn : null, cts.Token);
                    case "mqtt" or "tcp":
                        return await ConnectionDialer.DialAsync(transport,
                            new System.Net.DnsEndPoint(broker.Host, broker.IsDefaultPort || broker.Port < 0 ? 1883 : broker.Port), cts.Token);
                    default:
                        var wsUrl = broker.AbsolutePath == "/" ? new Uri(broker, "/mqtt") : broker;
                        session = new WebSocketSession();
                        await session.ConnectAsync(wsUrl.ToString(), cert, null, insecure, transport,
                            predicates.For(broker.Host), cts.Token, subProtocols: new[] { "mqtt" });
                        return new WebSocketByteStream(session, cts.Token);
                }
            }
        }
    }

    internal static string Text(byte[] payload)
    {
        try { return new UTF8Encoding(false, true).GetString(payload); }
        catch (DecoderFallbackException) { return $"[binary {payload.Length} bytes]"; }
    }

    internal static string Json(MqttMessage m)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("topic", m.Topic);
            try { w.WriteString("payload", new UTF8Encoding(false, true).GetString(m.Payload)); }
            catch (DecoderFallbackException) { w.WriteString("payloadBase64", Convert.ToBase64String(m.Payload)); }
            w.WriteNumber("qos", m.Qos);
            w.WriteBoolean("retain", m.Retain);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int? PositiveInt(string? raw, string flag)
    {
        if (raw is null) return null;
        if (!int.TryParse(raw, out var n) || n < 1)
            throw new CliUsageException($"{flag} expects a positive number, got '{raw}'.");
        return n;
    }
}
