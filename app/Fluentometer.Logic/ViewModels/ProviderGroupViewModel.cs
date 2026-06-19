using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluentometer.Logic.Ipc;

namespace Fluentometer.Logic.ViewModels;

/// <summary>
/// Represents one provider's contribution to the dashboard: a header (title, health)
/// and an ordered list of gauge view models driven by that provider's snapshots.
///
/// <para>
/// The gauge list is reconciled in place on each snapshot — gauges are updated by index,
/// added if the snapshot has more, and trimmed if it has fewer. This is the same strategy
/// as the original flat <see cref="UsageViewModel.ApplySnapshot"/> so <see cref="GaugeViewModel"/>
/// instances are reused across polls rather than replaced, preserving binding continuity.
/// </para>
///
/// <para>
/// The display <see cref="Title"/> is the provider ID capitalised ("Claude", "Gemini") — the
/// presentation layer can override this in XAML if a richer label is required.
/// </para>
/// </summary>
public sealed partial class ProviderGroupViewModel : ObservableObject
{
    /// <summary>Stable provider identifier (e.g. "claude", "gemini"). Matches <see cref="UsageSnapshot.Provider"/>.</summary>
    public string ProviderId { get; }

    /// <summary>Human-friendly display title for the section header (e.g. "Claude", "Gemini").</summary>
    public string Title { get; }

    /// <summary>
    /// Health string for this provider's section pill.
    /// Hard UI contract: exactly one of ok / degraded / needs-signin / error (kebab-case).
    /// </summary>
    [ObservableProperty] private string _health = "ok";

    /// <summary>
    /// Data source for this provider's last snapshot ("oauth", "jsonl", "demo", "local").
    /// Informational — not switched on by the dashboard.
    /// </summary>
    [ObservableProperty] private string _source = "";

    /// <summary>
    /// Gauges for this provider. The collection is mutated in place (no reference swap)
    /// so the outer dashboard <see cref="ItemsRepeater"/> sees CollectionChanged events
    /// rather than a full rebind.
    /// </summary>
    public ObservableCollection<GaugeViewModel> Gauges { get; } = new();

    /// <summary>
    /// Maps known provider IDs to their canonical brand display names.
    /// Keyed case-insensitively so "ChatGPT" and "chatgpt" both resolve correctly.
    /// </summary>
    private static readonly Dictionary<string, string> KnownDisplayNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "Claude",
            ["gemini"] = "Gemini",
            ["chatgpt"] = "ChatGPT",
        };

    /// <summary>
    /// Returns the human-readable display name for <paramref name="providerId"/>.
    /// Known providers (claude, gemini, chatgpt) return their exact brand name;
    /// unknown providers fall back to first-character uppercasing so future providers
    /// render reasonably without a code change.
    /// </summary>
    private static string DisplayNameFor(string providerId)
    {
        if (KnownDisplayNames.TryGetValue(providerId, out var name))
            return name;

        return providerId.Length > 0
            ? char.ToUpperInvariant(providerId[0]) + providerId[1..]
            : providerId;
    }

    public ProviderGroupViewModel(string providerId)
    {
        ProviderId = providerId;
        Title = DisplayNameFor(providerId);
    }

    /// <summary>
    /// Reconciles this group's gauge list with the incoming snapshot's gauges,
    /// using the same index-update/add/trim strategy as the original UsageViewModel.
    /// Must be called on the UI thread (mutates an ObservableCollection).
    /// </summary>
    internal void ApplySnapshot(UsageSnapshot snap)
    {
        Health = snap.Health;
        Source = snap.Source;

        // Update existing, add new (same index-reconcile as the original flat ApplySnapshot).
        for (var i = 0; i < snap.Gauges.Count; i++)
        {
            if (i < Gauges.Count)
                Gauges[i].Apply(snap.Gauges[i]);
            else
            {
                var vm = new GaugeViewModel();
                vm.Apply(snap.Gauges[i]);
                Gauges.Add(vm);
            }
        }

        // Trim surplus gauges.
        while (Gauges.Count > snap.Gauges.Count)
            Gauges.RemoveAt(Gauges.Count - 1);
    }
}
