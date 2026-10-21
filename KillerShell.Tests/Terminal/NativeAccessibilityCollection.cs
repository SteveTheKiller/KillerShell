using Xunit;

namespace KillerShell.Tests.Terminal;

// Windows UIA tracks desktop-wide window events, including other test processes.
// Run native clients after parallel window fixtures have finished.
[CollectionDefinition(NativeAccessibilityCollection.Name, DisableParallelization = true)]
public sealed class NativeAccessibilityCollection
{
    public const string Name = "Native accessibility";
}
