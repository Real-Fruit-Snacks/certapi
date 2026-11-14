# 15. Live Streaming (WebSocket, SSE, SignalR & MQTT)

Beyond request/response: connect to a **WebSocket** and exchange messages, or watch a **Server-Sent
Events (SSE)** stream arrive — and, on the command line, talk to a **SignalR** hub or an **MQTT**
broker the same way. Both reuse your selected client certificate and the insecure toggle, so
they work against mTLS (mutual Transport Layer Security) endpoints.

## In the app

The **Stream** button on the request line opens a live console. It picks the protocol from the URL
(Uniform Resource Locator) scheme:

- `ws://` or `wss://` → **WebSocket** — type a message and press Enter (or **Send**); every message
  the server sends back appears in the transcript, tagged with time and direction.
- `http://` or `https://` → **Server-Sent Events** — each event is appended as it arrives, with its
  event name (if any) and data.

**Connect** / **Disconnect** control the session; **Clear** empties the transcript. Closing the window
disconnects. A live indicator shows which mode the current URL will use.

## WebSocket on the CLI (command-line interface)

```powershell
# Send messages and wait for a set number of replies (good for scripts)
certapi ws wss://api.example.com/socket --cert "CN=My Client" -m '{"sub":"prices"}' --expect 3

# Pipe messages in on stdin
echo '{"ping":1}' | certapi ws wss://api.example.com/socket --expect 1
```

- **`-m, --message <text>`** — send this after connecting (repeatable). Lines piped on **stdin** are
  sent after the `-m` values; at a terminal, each line you type is sent as you enter it.
- **`--expect <n>`** — stop after receiving `n` messages (deterministic for scripts). `--expect 0`
  means send only. With no `--expect`, the client listens until the server closes or `Ctrl+C`.
- **`-q, --quiet`** — no connect/send/close notices on stderr.
- **`--workspace <file>`** — read trust pins and captured tokens from a workspace file instead of
  the live state.
- **`-H`** handshake headers, and the usual `--cert` / `--cert-file` / `--insecure`.
- The full transport story applies here too: a host pinned with `certapi trust add` needs no
  `--insecure`, a captured bearer token attaches to the handshake automatically
  (`--no-auto-token` turns that off), and the same `--proxy` / `--noproxy` / `--revocation`
  flags as `send` work unchanged.

Received messages go to **stdout**, a binary frame as `[binary N bytes]`; notices go to **stderr**.
Exit 0 on a clean close; exit 1 when the connection fails or breaks, including under `--expect`.

## Server-Sent Events on the CLI

```powershell
certapi sse https://api.example.com/events --cert "CN=My Client"
certapi sse https://api.example.com/stream --max-events 5 --json
```

- **`--max-events <n>`** — stop after `n` events. Only an event with data counts: a block with only
  `id:`, `retry:` or comment lines is not an event.
- **`--json`** — print one JSON (JavaScript Object Notation) object per event
  (`{event,data,id,retry}`), i.e. ndjson (newline-delimited JSON).
- **`-H`** request headers, plus the usual cert/insecure flags.
- **`-q, --quiet`** — no connecting/ended notices on stderr.
- **`--workspace <file>`** — read trust pins and captured tokens from a workspace file.
- Pins, automatic tokens, proxy control, and revocation checking work exactly as on `ws` above.

Events go to stdout; the connecting/ended notices to stderr. Exit 0 when the stream ends, 1 on a
transport error.

`sse` follows a redirect on the same origin (scheme, host and port), or from http to https on the
same host, and refuses one to another origin, because the request's headers and client certificate would go with it — connect to that
URL directly if it is the right one. `ws` does not follow redirects: a handshake answered with one
fails. Neither has redirect options.

## SignalR hubs on the CLI

`certapi signalr` speaks ASP.NET Core SignalR's JSON hub protocol over a WebSocket, with your client
certificate: it negotiates (following an Azure SignalR Service redirect), does the handshake, keeps
the connection alive, calls hub methods and prints the events the hub sends.

```powershell
# call a method and print its result
certapi signalr https://api.example.com/hubs/orders --cert "CN=My Client" --invoke 'GetOrder [42]'

# send a message, then wait for the event it causes
certapi signalr https://api.example.com/hubs/chat --cert "CN=My Client" `
  --send 'SendMessage ["ann", "hello"]' --on ReceiveMessage --count 1

# watch a hub for a minute; consume a streaming method
certapi signalr https://api.example.com/hubs/prices --on PriceChanged --seconds 60
certapi signalr https://api.example.com/hubs/feed --stream 'Counter [10, 500]'
```

- **`--invoke`** waits for each method's result (printed as JSON); **`--send`** does not;
  **`--stream`** prints each item of a streaming method. Arguments are a JSON array, or one JSON
  value for a single argument.
- Events print as `Event arg1 arg2`; **`--on`** filters them, **`--count`** and **`--seconds`** end
  the wait. **`--json`** prints the raw hub records instead.
- **`--access-token`** is the bearer token a JavaScript client's `accessTokenFactory` would return;
  a token captured for the host is used otherwise.
- A `HubException` from a method is printed and makes the exit code 1.

## MQTT brokers on the CLI

IoT brokers usually authenticate a device by its client certificate. `certapi mqtt` is a small MQTT
3.1.1 / 5.0 client that presents it, over TLS (`mqtts://`, port 8883) or a secure WebSocket
(`wss://…/mqtt`):

```powershell
# subscribe and print five messages
certapi mqtt mqtts://a1b2c3-ats.iot.eu-west-1.amazonaws.com --cert-file sensor-7.pfx --client-id sensor-7 -t 'sensors/#' --count 5

# publish, waiting for the broker's acknowledgement
certapi mqtt mqtts://broker.example.com --cert "CN=Device 7" --publish sensors/7/temp -m '{"c":21.5}' --qos 1

# over a WebSocket, as JSON lines
certapi mqtt wss://broker.example.com/mqtt --cert-file device.pfx -t alerts --seconds 60 --json
```

Messages print as `topic payload`. A broker that refuses the connection or a subscription says why
— a bad user name or password, not authorized, a client identifier the policy does not allow —
which is most of what goes wrong with a new device certificate. AWS IoT on port 443 needs
`--alpn x-amzn-mqtt-ca`; Azure IoT Hub wants `--username {hub}.azure-devices.net/{device}/?api-version=2021-04-12`.
See the [CLI Reference](21-CLI-Reference.md#mqtt) for every option.

## Try it locally

The [mock server](18-Mock-Server.md) serves both: `/sse` streams a few events, and a WebSocket echo
runs on any path. So you can exercise both clients without a real service:

```powershell
certapi mock --port 8770
certapi sse http://127.0.0.1:8770/sse --max-events 3
certapi ws  ws://127.0.0.1:8770/ws -m "hello" --expect 1
```

Next: [Response Views](16-Response-Views.md).
