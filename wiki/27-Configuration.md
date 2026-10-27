# 27. Configuration files & profiles

A real command in a corporate environment carries the same tedious half every time:

```powershell
certapi send https://api.internal/orders --cert "CN=My Client" --proxy socks5://127.0.0.1:1080 `
  --revocation online --timeout 60 --workspace .\suite.json
```

A **profile** puts that half in a file, so the command becomes:

```powershell
certapi send https://api.internal/orders --profile corp
```

## The file

The quickest start is `certapi config init`, which writes your per-user file with one profile and
every field below present but commented out (`--local` writes `certapi.config.json` in the current
directory instead; `--cert` and `--proxy` fill those two in). It never overwrites an existing file
without `--force`.

`certapi.config.json` — JSON, with `//` comments and trailing commas allowed, because people edit
these by hand:

```jsonc
{
  // Used when --profile is not given. Omit for "no default".
  "defaultProfile": "corp",
  "profiles": {
    "corp": {
      "cert": "CN=My Client",
      "store": "CurrentUser",
      "proxy": "socks5://127.0.0.1:1080",
      "noProxyList": "internal.corp,.test",
      "revocation": "online",
      "revocationStrict": false,
      "retry": 3,
      "timeout": 60,
      "insecure": false,
      "caFile": "corp-root.pem",
      "tlsMin": "1.2",
      "connectTimeout": 10,
      "workspace": "C:/work/suite.json",
      "headers": { "X-Env": "staging" }
    },
    "local": { "insecure": true }
  }
}
```

Every field is optional, and each command reads only the fields it understands — a profile with a
proxy in it never breaks `certapi certs`. Profiles are read when they are used, so a mistake (or an
unset `${env:…}`) in one profile never stops another from working.

| Field | The flag it stands for |
|---|---|
| `cert`, `store` | `--cert`, `--store` |
| `certFile`, `certPassword`, `keyFile` | `--cert-file`, `--cert-password`, `--key-file` |
| `proxy`, `proxyUser` | `--proxy`, `--proxy-user` |
| `noProxy` | `--no-proxy` (`true`) |
| `noProxyList` | `--noproxy` |
| `caFile` | `--ca-file` — a path or a list, relative to this file |
| `tlsMin` | `--tls-min` (`"1.2"` or `"1.3"`) |
| `connectTimeout` | `--connect-timeout`, in seconds |
| `revocation`, `revocationStrict` | `--revocation`, `--revocation-strict` |
| `retry` | `--retry` |
| `timeout` | `--timeout`, in seconds (not for `serve`, which takes `serve.timeout`) |
| `insecure` | `--insecure` |
| `workspace` | `--workspace` (not for `serve`) |
| `headers` | Standing headers added to every request `send` and `run` make (collections and HAR replay), unless the command line or the saved request sets the same name |
| `serve` | A whole gateway for `certapi serve` — [below](#a-gateway-in-a-profile) |

## Where it is found

First match wins:

1. **`--config <path>`** — named explicitly. A path that does not exist is an error, not a
   fall-through to some other file.
2. **`CERTAPI_CONFIG`** — an environment variable naming a file.
3. **`certapi.config.json`** — found by walking **up** from the working directory, so a
   per-repository configuration works from anywhere inside it.
4. **`%APPDATA%\certapi\config.json`** — your personal default.

`--no-config` ignores all four. That is how a run is made reproducible regardless of what happens
to sit in a parent directory — worth using in continuous integration.

## Precedence

**An explicitly typed flag always wins. The profile fills in what you did not type. The built-in
default stands when neither said anything.**

Naming one of a mutually exclusive pair counts as choosing it: `--cert-file` on the command line
suppresses a profile's `cert`, and `--no-proxy` suppresses a profile's `proxy`, rather than
colliding with it.

A typed list (`--ca-file`, and on `serve` `--allow` and `--cors <origins>`) replaces the profile's
list rather than adding to it. A switch a profile turns on is turned off for one run with its
`--no-` form: `--no-insecure`, `--no-revocation-strict`, and on `serve` `--no-browser`,
`--no-vhost`, `--no-tls`, `--no-rewrite-body`, `--no-forwarded`, `--no-auto-token`, `--no-windows-auth`, `--no-cors` and
`--no-cookie-jar`. Typing a switch and its `--no-` form together is a usage error. A profile's
`noProxy` is undone by naming a proxy with `--proxy <url>`.

Unknown fields inside `serve` and its sites are errors that name the field. Unknown fields at a
profile's top level are ignored — so check a new profile with `certapi config show`.

## A gateway in a profile

A profile's `serve` section describes a whole [local gateway](19-Local-Gateway.md), so a gateway
you start every day is one word long:

```jsonc
{
  "profiles": {
    "corp": {
      "cert": "CN=My Client",
      "caFile": "corp-root.pem",
      "serve": {
        "port": 8819,
        "vhost": true,                       // or a suffix: "gw.test"
        "browser": true,
        "token": "${env:GW_TOKEN}",
        "allow": ["*.corp"],                 // a host or a list
        "cookieJar": "gateway-session.bin",  // or true for memory only
        "sites": [
          { "name": "api",  "upstream": "https://api.corp" },
          { "name": "auth", "upstream": "https://login.corp" },
          { "name": "hr",   "upstream": "https://hr.corp:8443", "cert": "CN=svc-hr", "windowsAuth": true },
          { "name": "lab",  "upstream": "https://lab.corp", "insecure": false, "caFile": "lab-root.pem" }
        ]
      }
    }
  }
}
```

```powershell
certapi serve --profile corp --check    # validate it and print the table of sites
certapi serve --profile corp            # start it
```

The `serve` fields are `port`, `vhost`, `browser`, `cors` (`true`, an origin, or a list), `token`,
`allow`, `listen`, `cookieJar`, `autoToken`, `windowsAuth`, `windowsUser`, `windowsPassword`,
`forwarded`, `maxBody`, `timeout`, `tls`, `rewriteBody` and `sites`. `sites` is exactly the
[`--routes` file format](19-Local-Gateway.md#sites-with-their-own-certificate-and-settings---routes):
each site has a `name` (or `prefix`) and an `upstream`, and may set its own `cert`, `insecure`,
`caFile`, `revocation`, `windowsAuth`, `headers` and `sni`. An unknown field is an error naming it.

The certificate, proxy, CA files and revocation for the gateway as a whole come from the profile's
own top-level fields, as they do for every command. A flag typed on the command line wins; sites
typed on the command line (`--upstream`, `--routes`, or a positional upstream) replace the profile's
list whole, and the gateway says so.

## Secrets stay out of the file

Any value may contain `${env:NAME}`, read from the environment when the file is loaded:

```jsonc
{ "profiles": { "corp": { "proxyUser": "svc:${env:PROXY_PASSWORD}" } } }
```

So the file names *which* secret it needs, and the secret itself lives wherever your machine or
pipeline keeps secrets — the file stays safe to commit. A reference to a variable that is not set
is an error naming the profile, the field, and the variable, rather than a silently empty
credential. (The same idea inside a request is [`{{env:NAME}}`](09-Environments-and-Variables.md).)

## Seeing what is in effect

```powershell
certapi config path                    # which file, and by which rule
certapi config profiles                # the names defined, marking the default
certapi config show --profile corp     # the resolved profile, as a command sees it
```

`show` prints `(set)` for a password, a token, a proxy credential, or a header whose value came from
`${env:…}` or whose name looks like a credential, rather than the value — a diagnostic should never
be the thing that leaks a secret. Other fields print as resolved, including ones written as
`${env:…}`, so keep secrets in the fields meant for them. A `serve` section is shown as its settings and one line per site
(`serve.site  hr -> https://hr.corp:8443  (cert CN=svc-hr, windows-auth)`), with header values
hidden.

Next: [CLI Reference](21-CLI-Reference.md).
