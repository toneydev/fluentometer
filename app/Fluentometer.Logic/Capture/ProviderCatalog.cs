using System.Collections.Generic;
using Fluentometer.Logic.ViewModels;

namespace Fluentometer.Logic.Capture;

/// <summary>
/// Single source of truth for the set of providers Fluentometer recognizes and the
/// data-source hint copy shown for each in the Settings "Monitored services" section.
///
/// <para>
/// All recognized providers are treated equally — the list is alphabetical and no
/// provider is privileged. This replaces the previously duplicated hardcoded provider
/// arrays in <c>SettingsPage.BuildSwatch</c> and the inline hint <c>switch</c> in
/// <c>SettingsPage.BuildProviderRow</c>.
/// </para>
///
/// Pure (no I/O); display names resolve through the canonical
/// <see cref="ProviderGroupViewModel.DisplayNameFor"/> map.
/// </summary>
public static class ProviderCatalog
{
    /// <summary>
    /// Provider IDs Fluentometer recognizes, in canonical (alphabetical) display order.
    /// </summary>
    public static IReadOnlyList<string> RecognizedIds { get; } =
        new[] { "chatgpt", "claude", "gemini" };

    /// <summary>
    /// Returns the data-source hint caption for <paramref name="providerId"/>.
    /// Known providers return their server-truth hint; unknown providers fall back to
    /// the local-estimate wording so a future provider renders without a code change.
    /// </summary>
    public static string SourceHint(string providerId) => providerId switch
    {
        "claude" => "Claude — server data · requires Claude Code",
        "chatgpt" => "ChatGPT — server data · requires Codex CLI",
        "gemini" => "Gemini — server data · requires Gemini CLI",
        _ => $"{ProviderGroupViewModel.DisplayNameFor(providerId)} — local estimate (no API key required)",
    };
}
