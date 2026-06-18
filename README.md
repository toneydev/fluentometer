# Fluentometer

An elegant Windows desktop monitor for your **Claude usage** — live **5-hour** and **weekly**
limits shown as animated, gradient **Fluent 2** gauges. Built with **WinUI 3** on a
**provider-agnostic C# metrics engine** that runs in-process.

![Fluentometer dashboard showing live Claude 5-hour and weekly usage gauges](assets/demo.gif)

> Status: **v1** — Claude monitoring only (the architecture is built to add other services
> later). Ships **unsigned** (first-run SmartScreen prompt → "More info" → "Run anyway").

## Download

**[⬇ Download the latest release](https://github.com/toneydev/fluentometer/releases/latest)** — unzip and run `Fluentometer.exe`. No install, no .NET required.

## Features

- Live **5-hour**, **weekly**, and **per-model weekly** usage gauges with smooth Composition motion.
- **Zero-config** if you're already signed into Claude Code — Fluentometer reuses your existing
  session, no separate login.
- **8 rich gradient themes** with complementary accent bars; switch live in Settings.
- **System-tray** presence (optional launch-on-login) plus a full dashboard window.
- Reset countdowns, plan label, and a connection indicator.

## How it works

A C# capture engine (`app/Fluentometer.Logic/Capture/`) does all the data work in-process, behind
the `IUsageClient` seam the UI consumes. For usage data it uses a **hybrid** source:

- **Primary:** the authoritative Anthropic `/api/oauth/usage` endpoint (the same data behind
  Claude Code's `/usage`), reusing the OAuth token already on disk. Polled no more than once every
  ~3 minutes.
- **Fallback:** local Claude Code session logs (`~/.claude/projects/**/*.jsonl`) when the endpoint
  is unavailable, clearly labelled as an estimate.

The app **never transmits your data anywhere** except your own authenticated requests to
`api.anthropic.com` over verified TLS. Your token is read, never logged or copied. See
[`SECURITY.md`](SECURITY.md).

If you're not signed into Claude Code, the dashboard shows a friendly "sign in" prompt instead of
data.

## Requirements

- **Windows 10 1809+ / Windows 11.**
- **For live data:** be signed into Claude Code — Fluentometer reuses that session (no separate login).
- **To build from source:** [.NET 10 SDK](https://dotnet.microsoft.com/) and PowerShell 7+.

## Build & run from source

```powershell
# 1. Build the WinUI app (capture is in-process — there is no sidecar to build or copy)
dotnet build app/Fluentometer/Fluentometer.csproj -c Debug

# 2. Run
& "app/Fluentometer/bin/Debug/net10.0-windows10.0.19041.0/win-x64/Fluentometer.exe"
```

Run the full test + lint gate (.NET) before committing:

```powershell
dotnet format --verify-no-changes
dotnet build -warnaserror
dotnet test
```

> When iterating, stop any running instance before rebuilding (it locks the output):
> `Get-Process Fluentometer | Stop-Process -Force`

## Project structure

| Path | What |
|------|------|
| `app/Fluentometer/` | WinUI 3 app (views, controls, tray, theming) |
| `app/Fluentometer.Logic/` | Testable C# logic: `Capture/` (credential read, OAuth polling, JSONL fallback, poll loop), `Store/` (snapshot cache), DTOs, ViewModels, theming, formatting |
| `app/Fluentometer.Tests/` | xUnit tests |

## License & contributing

Open-source security posture: the design is publicly reviewable and no secrets live in the repo
or its history. See [`SECURITY.md`](SECURITY.md) for the threat model and how credentials are
handled.
