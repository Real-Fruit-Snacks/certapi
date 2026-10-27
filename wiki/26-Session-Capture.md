# 26. Session Capture

Some sites don't hand you a token from a simple API (application programming interface) call — you
log in through a web page, and the session lives in **cookies** (an ASP.NET session, a single sign-on
(SSO) cookie) or a bearer token minted deep inside a JavaScript flow. **Session capture** lets you log
in once, in a real browser, and reuse that authenticated session on every later request — in the app
and headless on the command line.

> Use this only against systems you are authorized to test.

## What it does

**Capture session…** (in the status bar, next to *Mock server…*) opens a browser window. You navigate
to the login page and sign in normally. As you do, the app:

- **watches every request/response** the page makes — each call is fulfilled through the app's own
  client, so your selected client certificate is presented (mutual Transport Layer Security, or mTLS,
  works here just as it does for a normal send);
- **captures any bearer token** it sees, scoped to the origin it came from;
- on **Finish & save**, reads the **session cookies** the browser accumulated (including HttpOnly
  cookies — the ones that usually *are* the session).

Captured cookies and tokens are stored per origin (scheme + host + port) and **attached automatically**
to later requests to that origin — no copy-paste.

Nothing is kept until you press **Finish & save**. **Cancel**, or closing the window, discards the
tokens and cookies seen during the capture.

## Your password is never captured

You type your password into the **site's own login form** inside the browser. The app only ever sees
the *result* — the cookies and tokens the site issues. It never reads, stores, or autofills your
password.

## Saving the calls you saw

When you finish, the window lists the API calls observed during login (deduplicated by method and
path) and offers to save them as a new collection of requests. Those requests default to **Auto**
auth, so replaying them uses the session you just captured — a starting library that is already
authenticated.

## Reusing the session headless

Because captured cookies live in the workspace, `certapi` attaches them too. A session you captured by
clicking through a login in the app replays in continuous integration (CI):

```powershell
# after capturing in the app (which saved cookies for the origin into your workspace):
certapi send https://intranet.corp/api/me            # the captured cookies go out automatically
certapi run "intranet/Get profile"                   # so do saved requests
```

`--no-auto-token` also suppresses cookie attach for that invocation, and the workspace's
*Automatically use captured cookies* switch (on the session chip) is the global control.

## Managing a captured session

The **session chip** in the status bar shows what's captured for the current website — a token,
a cookie count, or both. Click it to clear the token, clear the cookies, or toggle automatic use of
either.

## Try it against the mock

The local [mock server](18-Mock-Server.md) has a `/cookie-auth` route that sets a session cookie and
then reports you as authenticated once you send it back — a safe target to see capture-and-attach work
end to end.

## Security note

Captured cookies and tokens are stored in your workspace (`%AppData%\CertApiTester\state.json`)
**encrypted** with the Windows Data Protection API (DPAPI), scoped to the Windows user who captured
them — the same protection automatic tokens get. Session cookies are still as sensitive as
passwords, though: a captured session lets someone act as you on that site for as long as it's valid,
so clear a captured session from the chip when you're done, and remember that a workspace file
carried to another Windows user or another machine loses its captured cookies and tokens (they can't
be decrypted there) while everything else in it still loads. See
[Authentication](08-Authentication.md) and [Capturing Values](12-Capturing-Values.md).

---

Back to the [handbook home](README.md).
