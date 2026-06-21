# Fluentometer

An elegant Windows desktop monitor for your **AI usage** — live **5-hour** and **weekly** limits
shown as animated, gradient **Fluent 2** gauges. Built with **WinUI 3** on a **provider-agnostic
C# metrics engine** that runs in-process and **auto-detects** which AI tools you're signed into.

<p align="center">
  <img src="assets/demo.gif" alt="Fluentometer dashboard showing live Claude, ChatGPT, and Gemini usage gauges" width="266">
</p>

> **Providers:** **Claude**, **ChatGPT**, and **Gemini** all show authoritative server-side usage,
> read from each tool's own signed-in CLI session. Each provider appears automatically when its CLI is
> signed in. Ships **unsigned** (first-run SmartScreen → "More info" → "Run anyway").

## Download

**[⬇ Download the latest release](https://github.com/toneydev/fluentometer/releases/latest)** — unzip and run `Fluentometer.exe`. No install, no .NET required.

## What it does

Fluentometer sits in your system tray and shows, at a glance, how much of your AI usage limits
you've consumed and when they reset. It **auto-detects** the AI CLIs you're already signed into and
adds a gauge group for each — no API keys, no separate logins, no configuration. Sign into one tool
or all three.

For each provider it shows a card group:

- A **5-hour** (rolling session) gauge and a **weekly** gauge, plus **per-model weekly** gauges for
  Claude where the plan reports them.
- A big **percent used**, the **limit label**, and a live **reset countdown** ("resets in 4h 12m").
- Your **plan** (e.g. "Claude Pro", "ChatGPT Plus") and a **status indicator** that pulses red if
  data stops refreshing.

## Providers & how it works

A C# capture engine (`app/Fluentometer.Logic/Capture/`) does all the data work in-process behind the
`IUsageProvider` seam. Each provider is **detected**, **polled**, and **rendered independently**, and
reads only credentials you already have on disk — **read-only, never written, never logged, never
copied.** All network calls go over OS-verified **TLS** to the provider's own host.

| Provider | Usage data | Detected via | If unavailable |
|----------|-----------|--------------|----------------|
| **Claude** | Authoritative Anthropic `/api/oauth/usage` (the data behind Claude Code's `/usage`) | Claude Code's OAuth token in `~/.claude` | Falls back to local Claude Code session logs (`~/.claude/projects/**/*.jsonl`), clearly labelled as an estimate |
| **ChatGPT** | Authoritative usage from OpenAI's Codex backend, reusing your Codex session | OpenAI Codex CLI credential in `~/.codex` (honors `CODEX_HOME`); only when signed in with a ChatGPT subscription, not an API key | Shows a degraded state (no local fallback exists) |
| **Gemini** | Authoritative quota from Google's Code Assist backend (the data behind Gemini CLI's `/usage`), reusing your Gemini CLI session | Gemini CLI OAuth credential in `~/.gemini`; only when signed in with a Google account, not an API key or Vertex AI | Shows a degraded state (no local fallback exists) |

Polling is rate-limited to **no more than once every ~3 minutes** per provider (configurable floor),
with automatic back-off on rate limits.

Fluentometer **never transmits your data anywhere** except your own authenticated requests to each
provider's API, and only to display the result back to you. See [`SECURITY.md`](SECURITY.md) for the
full threat model, the exact endpoints called per provider, and how credentials are handled.

## Dashboard states

The dashboard adapts to each provider's health:

- **Live** — animated gauges with current utilization.
- **Sign in** — if you're not signed into a detected tool, a friendly prompt replaces its data
  instead of showing zeros.
- **Degraded** — when authoritative data is briefly unavailable, Claude shows a labelled local
  estimate; ChatGPT and Gemini show a degraded card (they have no local fallback).
- **Stale / unreachable** — if refreshes start failing or no fresh update has landed in a while
  (relative to your poll interval), the status indicator **slowly pulses red** and a hover tooltip
  explains which provider is stuck and how long it's been — so a silently frozen gauge can't masquerade
  as live data. The last-known values stay on screen, and the indicator returns to steady green as
  soon as a good update arrives.
- **Refresh** — a manual refresh button requests a fresh snapshot on demand.

## Settings

Open Settings from the dashboard (gear icon):

- **Theme** — 9 rich gradient palettes, including a **Brand colors** mode that tints each provider's
  gauge with its own brand gradient; switch live.
- **Gradient direction** — choose whether each bar's gradient runs bright→deep or deep→bright.
- **Density** — choose how much space each usage card takes: **Comfortable**, **Compact**, or
  **Mini**. Mini condenses each card to a single row with a slim usage bar — label, reset
  countdown, and percent on one line — fitting many more providers on screen at once; switch live.
- **Startup** — optionally launch Fluentometer when you log in.
- **Poll interval** — how often to check for updated usage (slider, **3-minute minimum**).
- **Monitored services** — every supported provider (Claude, ChatGPT, Gemini) is listed equally;
  turn each on or off for the dashboard. A provider not detected on your machine shows a quiet
  "not detected" note (its toggle stays available), and newly detected tools surface a one-time
  notification.
- **Demonstration mode** — animate the gauges with simulated data for demos and screenshots
  (session-only; resets on restart, and renders every supported provider).

## Requirements

- **Windows 10 1809+ / Windows 11.**
- **For live data:** be signed into the tool(s) you want to monitor — Claude Code, OpenAI Codex CLI,
  and/or Gemini CLI. Fluentometer reuses those sessions (no separate login).
- **To build from source:** [.NET 10 SDK](https://dotnet.microsoft.com/) and PowerShell 7+.

## Data & uninstall

Settings and the cached last snapshot live under `%LOCALAPPDATA%\Fluentometer` (per-user). The
portable build needs no installation — delete the unzipped folder to remove the app, and delete
`%LOCALAPPDATA%\Fluentometer` to remove all of its data. No credentials are ever stored there.

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
| `app/Fluentometer/` | WinUI 3 app — views, controls, tray, theming, Composition motion |
| `app/Fluentometer.Logic/` | Testable C# logic: `Capture/` (per-provider credential read, detection, OAuth/usage polling, provider registry, poll loop), `Store/` (snapshot cache), `Settings/` (provider enable/disable), DTOs, ViewModels, theming, formatting |
| `app/Fluentometer.Tests/` | xUnit tests |

## License & contributing

Open-source security posture: the design is publicly reviewable and no secrets live in the repo or
its history. See [`SECURITY.md`](SECURITY.md) for the threat model and how credentials are handled
per provider.
