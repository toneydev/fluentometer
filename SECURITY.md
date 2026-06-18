# Security Policy

## Reporting Vulnerabilities

**Do not open a public GitHub issue for security vulnerabilities.**

Please report security issues via a
[GitHub Security Advisory](https://github.com/search?q=fluentometer&type=repositories)
— navigate to the repository's **Security** tab and choose **"Report a vulnerability"**
to open a private advisory. The maintainer will acknowledge reports within seven days.

If you cannot use GitHub's private advisory flow, describe the issue in enough detail that
it can be reproduced and contact information for follow-up.

---

## What Fluentometer Does (Trust Model)

Fluentometer is a desktop usage-monitor for Claude Code. It is a **single WinUI 3 process**
(`app/`) with two internal layers:

| Layer | Role |
|---|---|
| **Presentation** (`app/Fluentometer/`) | Tray, dashboard, MVVM, theming. Renders gauge data; holds no credentials of its own. |
| **Capture engine** (`app/Fluentometer.Logic/Capture/`, `Store/`) | Runs in-process behind the `IUsageClient` seam. Reads the user's existing Claude Code OAuth token from disk, calls `api.anthropic.com/api/oauth/usage` over verified TLS, falls back to local JSONL, and caches the latest snapshot. |

> **Architecture note (2026-06-18):** earlier versions split capture into a separate **Rust
> sidecar** that talked to the UI over a named pipe. That sidecar and its IPC channel have been
> **removed** — capture now runs in-process in C#. This **eliminates the local named-pipe attack
> surface** entirely. The previous process split did **not** cross a privilege boundary (both
> processes ran as the same user, reading the same user's token), so collapsing it does not lower
> the effective trust boundary: a compromise of the process is, as before, a compromise within the
> user's own session. The token still never leaves the machine except as an authenticated request
> to `api.anthropic.com`.

The OAuth token belongs to Claude Code. Fluentometer reads it solely to request usage data
on behalf of that same authenticated user and display it to that user. The token is never
copied to another location, never logged, and never transmitted to any host other than
`api.anthropic.com`.

---

## Credential Handling

**Fluentometer does not store any credentials.**

The Claude Code OAuth token lives in `%USERPROFILE%\.claude\.credentials.json`, which is
created and managed entirely by Claude Code. Fluentometer reads that file at poll time and
holds the token value in memory only for the duration of the HTTP request. It does not:

- Copy or cache the token to any other file
- Write the token to the Windows registry
- Log the token to any sink (see "No Credential Logging" below)
- Transmit the token to any host other than `api.anthropic.com`

The `RedactedString` type in `app/Fluentometer.Logic/Capture/Credentials.cs` wraps the token so
its `ToString()` returns `***` and the raw value is reachable only through an explicit `Expose()`
call — ensuring the token cannot appear in logs, formatted strings, or error messages even if a
containing record is inadvertently stringified.

> **Why this is read-only, not Windows Credential Manager:** Fluentometer's general rule is that
> secrets *it owns* go in the Credential Manager (DPAPI). It owns none. The Claude Code token is
> a foreign credential that Fluentometer only **reads** from Claude Code's own file; moving or
> copying it into another store would be both unnecessary and a larger footprint for the secret.

**Settings and local state** are stored under `%LOCALAPPDATA%\Fluentometer\`:

| File | Contents |
|---|---|
| `settings.json` | Theme preference, poll interval, offline-only flag — no secrets |
| `last-snapshot.json` | Most-recent usage snapshot (utilization fractions and timestamps) — no secrets |

No credentials are stored under `%LOCALAPPDATA%\Fluentometer\`, nor anywhere else by the app.

---

## Network Security

All network calls are made in-process by the capture engine, using a single
`System.Net.Http.HttpClient`. There is exactly one outbound destination.

### TLS Certificate Verification

The `HttpClient` uses .NET's default handler, which validates server certificates against the
operating system's trusted root store. Certificate verification is never disabled:

- No `ServerCertificateCustomValidationCallback` is registered.
- `DangerousAcceptAnyServerCertificateValidator` is never used.
- No certificate-pinning bypass or "accept all" shim exists in the codebase.

All HTTP requests target `https://api.anthropic.com`. Plain HTTP is never used for API calls.

### Endpoint Transparency

The capture engine calls `https://api.anthropic.com/api/oauth/usage`.

**Honest disclosure:** this endpoint is not listed in Anthropic's public API documentation.
It is the same endpoint the Claude Code CLI itself uses to retrieve the current user's usage
data. Because it is undocumented:

- Anthropic may change its response shape, authentication requirements, or availability
  without notice.
- The JSONL fallback (`app/Fluentometer.Logic/Capture/Jsonl.cs`) mitigates an outage: when the
  endpoint returns a non-2xx response or the request fails, the engine falls back to counting
  token events from `%USERPROFILE%\.claude\projects\**\*.jsonl` to provide a local estimate.
  The estimate may differ from the server-authoritative value.
- Fluentometer sets `User-Agent: claude-code/<version>` on every request, mirroring what
  the CLI sends (`OauthConstants.ClaudeCodeVersion`).

Users should be aware that estimated usage figures from the JSONL fallback are
**approximations** and the live figures from the OAuth endpoint are the authoritative source.

### Poll Rate

The minimum poll interval is 180 seconds, enforced in
`app/Fluentometer.Logic/Capture/LiveUsageClient.cs`. The UI settings page clamps any user-chosen
interval to the same floor before issuing a `setPollInterval` command. A `refreshNow` command
issued within the 180-second floor is silently discarded rather than bypassing the rate.

---

## No Credential Logging

- The `RedactedString` wrapper ensures the OAuth token never appears in any log line, formatted
  string, or error message — its `ToString()` is `***`.
- `Expose()` is called at exactly one site — building the `Authorization: Bearer` header in
  `OauthUsageClient` — and is never interpolated into a log, format string, or exception message.
- `UsageSnapshot`, the only type cached to disk, has no credential fields.

There is no separate IPC channel and no inter-process log surface; capture runs inside the app
process, so the only relevant logging sink is the app's own diagnostics, which never receive the
token.

---

## Local Attack Surface

Because capture is in-process, there is **no local IPC endpoint** (named pipe, socket, or shared
memory) for another local process to connect to, send commands to, or read snapshots from. The
named-pipe server and its hardening (remote-client rejection, per-user DACL, command-length caps,
malformed-input dropping) that existed in the prior sidecar architecture are gone along with the
sidecar — the surface they protected no longer exists.

The app reads two locations owned by the current user (`~/.claude/.credentials.json` and
`~/.claude/projects/`) and writes only under `%LOCALAPPDATA%\Fluentometer\` and a single `HKCU`
launch-on-login value (below).

---

## Launch-on-Login

The optional "launch on login" feature writes a single registry value to
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. It uses `Registry.CurrentUser`
(`HKCU`) exclusively — never `HKLM`. The value contains only the path to the application
executable. No credentials are involved.

---

## User-Scoped Storage

All state written by Fluentometer lives under user-owned paths:

- `%LOCALAPPDATA%\Fluentometer\` — settings and snapshot cache (no secrets)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — launch-on-login value (no secrets)

Nothing is written to `Program Files`, `ProgramData`, `HKEY_LOCAL_MACHINE`, or any
system-wide location.

---

## Dependency Auditability

The .NET dependency graph is reproducible from the `*.csproj` files and pinned by the committed
`packages.lock.json` files. There is no longer any native/Rust dependency graph.

Key dependencies and their security relevance:

| Dependency | Role | Security Note |
|---|---|---|
| `System.Net.Http.HttpClient` (BCL) | HTTP client | OS-trusted root store; certificate verification on by default; no custom validation callback |
| `System.Text.Json` (BCL) | JSON parsing | Deserialises only known schema types; unknown fields are ignored |
| `Microsoft.WindowsAppSDK` | WinUI 3 framework | UI rendering |
| `CommunityToolkit.Mvvm` | MVVM helpers | UI state only; no credential or network access |

---

## Open-Source Posture

Fluentometer is open source. Its security design is intended to be correct and publicly
reviewable:

- No secrets, API keys, or environment-specific credentials are present in the source code
  or git history. Test fixtures use clearly-redacted placeholder values only.
- Security-sensitive design decisions (read-only credential access, TLS configuration,
  no local IPC surface, no credential logging) are documented here for external reviewer
  inspection.

---

## Release Integrity

Official release binaries should be Authenticode-signed so that Windows SmartScreen
shows a verified publisher name rather than "Unknown publisher". Unsigned binaries are
acceptable for pre-release builds reviewed directly from source.

- Signing is a manual release step performed by the maintainer with a user-supplied
  Authenticode certificate (recommended: Azure Trusted Signing).
- No signing certificate, `.pfx` file, or password is present in this repository.

---

## Supported Versions

| Version | Supported |
|---|---|
| Latest (`main`) | Yes |
| Earlier releases | No — update to the latest release |
