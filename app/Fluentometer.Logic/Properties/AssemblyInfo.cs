using System.Runtime.CompilerServices;

// Allow the test project to access internal members — specifically
// FileThemeStore(string baseDirOverride) used by FileThemeStoreTests.
[assembly: InternalsVisibleTo("Fluentometer.Tests")]
