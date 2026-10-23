using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class DemoProfilesTests
    {
        [Fact]
        public void ProfilesAppearInTheDemoFileTreeWithMatchingScriptContents()
        {
            Assert.Equal(2, DemoProfiles.All.Count);
            foreach (var profile in DemoProfiles.All)
            {
                Assert.NotNull(DemoFs.Find(profile.Path));
                Assert.Contains("function prompt", DemoProfiles.TextFor(profile.Path));
            }
        }

        [Fact]
        public void ProfileEditsRemainInMemoryAndRejectOtherPaths()
        {
            string original = DemoProfiles.TextFor(DemoProfiles.PowerShellPath)!;
            DemoTextStore.Open(DemoProfiles.PowerShellPath, original);
            try
            {
                Assert.True(DemoTextStore.TrySave(DemoProfiles.PowerShellPath, "# Edited demo profile"));
                Assert.Equal("# Edited demo profile", DemoTextStore.Open(DemoProfiles.PowerShellPath, original));
                Assert.False(DemoTextStore.TrySave(@"C:\Users\Other\profile.ps1", "unexpected"));
                Assert.Null(DemoProfiles.TextFor(@"C:\Users\Other\profile.ps1"));
            }
            finally { DemoTextStore.TrySave(DemoProfiles.PowerShellPath, original); }
        }
    }
}
