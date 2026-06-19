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

All network calls are made in-process by the capture engine. A single shared `HttpClient`
instance issues absolute-URL requests to the enabled providers' hosts — `api.anthropic.com`
for Claude, and `chatgpt.com` for ChatGPT when the Codex credential is detected. TLS certificate
verification is always enabled on this client; no custom `ServerCertificateCustomValidationCallback`
is registered. Outbound destinations are limited to the set of enabled providers.

### TLS Certificate Verification

The `HttpClient` uses .NET's default handler, which validates server certificates against the
operating system's trusted root store. Certificate verification is never disabled:

- No `ServerCertificateCustomValidationCallback` is registered.
- `DangerousAcceptAnyServerCertificateValidator` is never used.
- No certificate-pinning bypass or "accept all" shim exists in the codebase.

All HTTP requests target `https://api.anthropic.com` (Claude) and, when the ChatGPT provider
is active, `https://chatgpt.com`. Plain HTTP is never used for API calls.

### Endpoint Transparency

The capture engine calls `https://api.anthropic.com/api/oauth/usage` (Claude provider).

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

When the ChatGPT provider is active, the capture engine additionally calls
`https://chatgpt.com/backend-api/wham/usage` with `Authorization: Bearer <access_token>` and
`ChatGPT-Account-Id: <account_id>`, reading the Codex CLI token from
`%USERPROFILE%\.codex\auth.json` (or `$CODEX_HOME\auth.json`).

**Honest disclosure for the ChatGPT provider:**

- This endpoint (`/backend-api/wham/usage`) is not listed in any public OpenAI API documentation.
  It is an internal endpoint used by OpenAI's own Codex CLI. OpenAI may change its shape,
  authentication requirements, or availability without notice. The health field will surface
  `degraded` or `error` if the endpoint becomes unavailable.
- The Codex CLI credential is a ChatGPT OAuth JWT stored in plaintext by the Codex CLI itself.
  Fluentometer reads it solely to make a usage request on behalf of the same authenticated user
  and display the result to that user. The token is never copied to another location, never
  logged, and never transmitted to any host other than `chatgpt.com`.
- Users who have Codex CLI installed but use only an API key (not a ChatGPT subscription) will
  not see a ChatGPT gauge, because the detector gates on `auth_mode == "chatgpt"`.
- No fallback local-estimate path exists for ChatGPT (unlike Claude's JSONL fallback). If the
  endpoint is unavailable, the gauge shows a `degraded` health state with no utilization figure.

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

The app reads several locations owned by the current user (see "Provider Detection Probes" below
for the full list) and writes only under `%LOCALAPPDATA%\Fluentometer\` and a single `HKCU`
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

- `%LOCALAPPDATA%\Fluentometer\settings.json` — theme, poll interval, offline-only flag (no secrets)
- `%LOCALAPPDATA%\Fluentometer\last-snapshot.json` — most-recent usage snapshot (no secrets)
- `%LOCALAPPDATA%\Fluentometer\providers.json` — per-provider enable/disable flags and "seen" list (no secrets; provider IDs only, never tokens)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — launch-on-login value (no secrets)

Nothing is written to `Program Files`, `ProgramData`, `HKEY_LOCAL_MACHINE`, or any
system-wide location.

---

## Provider Detection Probes

Fluentometer auto-detects which AI tools are installed and signed-in by probing a fixed, explicit
set of paths owned by the current user. This section documents every path probed, exactly what
is (and is not) read, and the security invariants that govern the probes.

### Detection Contract

Detection answers only "is this provider installed AND does a signed-in session appear to exist?"
It does **not** read or hold any credential value. The result type is
`ProviderDetectionResult(ProviderDetectionStatus, string? ProviderDisplayName)` — a discriminated
union with no credential fields (G-8). All detection I/O runs off the UI thread (G-9).

### Read-Only Guarantee

No detector writes to any probed path. Detectors are read-only by construction; there is no
code path in any `IProviderDetector` implementation that calls `File.WriteAllText`,
`File.AppendAllText`, `Directory.CreateDirectory`, or any other mutating I/O on the probed
locations (G-4).

### Reparse-Point Rejection

Before reading any file, each detector calls `File.GetAttributes(path)` in a single syscall that
combines the existence check and the reparse-point check atomically. If the path carries
`FileAttributes.ReparsePoint` (symlink or NTFS junction), the detector immediately returns
`NotFound` without reading any file content (G-6). Using a single `GetAttributes` call eliminates
the TOCTOU race that would exist if `File.Exists` were called first and then `GetAttributes`.

### No Recursive Directory Walks

Every probed path is a compile-time constant derived from `Environment.SpecialFolder.UserProfile`
or `Environment.SpecialFolder.LocalApplicationData`. No `Directory.EnumerateFiles`,
`Directory.EnumerateDirectories`, or recursive walk is performed during detection (G-7).

### Probed Paths

| Path | What IS read | What is NEVER read |
|------|-------------|-------------------|
| `%USERPROFILE%\.claude\.credentials.json` | Structural presence of the `claudeAiOauth` JSON key only — the detector checks that the key exists, not its contents | `accessToken`, `refreshToken`, `expiresAt` — these fields are never accessed by the detector DTO; only `ClaudeCredentialReader` reads them at poll time, wrapped immediately in `RedactedString` |
| `%USERPROFILE%\.gemini\settings.json` | `selectedAuthType` string value (e.g. `"oauth-personal"`) — used to confirm the user has authenticated and to label the gauge plan | Token fields, API key values, gcloud credential paths, or any other field in the file |
| `%USERPROFILE%\.codex\auth.json` (or `$CODEX_HOME\auth.json` when `CODEX_HOME` is set) | `auth_mode` string (e.g. `"chatgpt"`) and structural presence of a `tokens` block — used to confirm the user has a ChatGPT subscription session, not a bare API-key Codex session | `tokens.access_token`, `tokens.refresh_token` (JWTs) — these fields are never accessed by the detector DTO; only `CodexCredentialReader` reads them at poll time, wrapped immediately in `RedactedString`. `account_id`, `email`, `last_refresh` are also not read during detection. |

The `selectedAuthType` value from `settings.json` is a non-secret configuration string (one of
`oauth-personal`, `oauth-workspace`, `api-key`, `vertex-ai`). It is not a credential: it
describes which auth mechanism is configured, not the credential itself.

The `auth_mode` value from Codex's `auth.json` (e.g. `"chatgpt"`) is similarly a non-secret
configuration string that identifies the authentication mode, not any credential value.
Reading it during detection is within the G-2 bound. The path is resolved by preferring
`$CODEX_HOME` (if set) with the same GetAttributes + reparse-point guard applied to the
env-var-supplied path before any read occurs.

### Paths Explicitly NOT Probed

The following paths were evaluated and rejected during the security design review. They are
documented here so reviewers can confirm the implementation stays within the stated bounds.

| Path / Variable | Reason not probed |
|----------------|------------------|
| `%APPDATA%\gcloud\application_default_credentials.json` | Deferred to a future version; would require a narrow DTO reading `type` only |
| `%APPDATA%\gcloud\credentials.db` | Contains full OAuth refresh tokens in SQLite format — **never read**, escalation required before any access |
| `GEMINI_API_KEY` environment variable value | The raw API key value is **never extracted**; name-presence check deferred; escalation required before the value is used |
| VS Code extension storage | Unstable format, not publicly documented — deferred |
| Any other application's PasswordVault entries | **Never accessed** — Fluentometer never reads another application's DPAPI-encrypted credential store |

### Gemini Provider: Local Estimate Only, No Network Call

`GeminiProvider` (the usage-data provider activated after detection) makes **no network calls**.
It produces a local-estimate snapshot with `Source = "local"` and `Utilization = null` on all
gauges. There is no Gemini usage endpoint accessible to third-party clients; the gauge is present
to confirm Gemini is active, not to report server-authoritative utilization. The `IsEstimate`
label path in the UI communicates this clearly to the user.

Because there is no network call, no Gemini credential value (API key, OAuth token, or gcloud
token) is ever transmitted anywhere by this provider.

### Secret-Field Ban List

The following values must never appear in any log, cache file, telemetry payload, or serialized
structure:

- OAuth access tokens, refresh tokens, or ID tokens
- API key values
- Raw environment variable values for any credential-related key
- Raw credential file contents (partial or full)
- `client_secret` values
- Windows account usernames embedded in full filesystem paths in log output

All detection catch blocks return `NotFound` or `Error` status without logging exception messages,
since exception messages on file-access failures can contain path details (G-11).

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
- `Fluentometer.exe` should be signed before the installer is compiled, so that installed
  binaries carry the signature.
- The compiled installer (`Fluentometer-Setup-*.exe`) should also be signed.
- No signing certificate, `.pfx` file, or password is present in this repository.

For the exact `signtool` command sequence and build steps, see
[`installer/README.md`](installer/README.md).

---

## Supported Versions

| Version | Supported |
|---|---|
| Latest (`main`) | Yes |
| Earlier releases | No — update to the latest release |
