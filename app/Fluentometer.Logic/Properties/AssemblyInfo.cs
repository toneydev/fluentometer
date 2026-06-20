using System.Runtime.CompilerServices;

// Allow the test project to access internal members — specifically
// FileThemeStore(string baseDirOverride) used by FileThemeStoreTests.
[assembly: InternalsVisibleTo("Fluentometer.Tests")]

// Allow the WinUI presentation project to consume internal members — specifically
// ProviderGroupViewModel.DisplayNameFor used by SettingsPage to resolve provider labels
// through the single canonical source of truth rather than re-deriving them in code-behind.
[assembly: InternalsVisibleTo("Fluentometer")]
