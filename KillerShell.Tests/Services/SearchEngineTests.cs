using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class SearchEngineTests
    {
        [Theory]
        [InlineData("error.log", "*.log", false, true)]
        [InlineData("ERROR.LOG", "*.log", false, true)]
        [InlineData("ERROR.LOG", "*.log", true, false)]
        [InlineData("notes.txt", "n?tes.*", true, true)]
        [InlineData("notes.txt", "*.log", false, false)]
        public void MatchesWildcard_UsesExpectedCaseAndWildcardRules(
            string input, string pattern, bool caseSensitive, bool expected)
            => Assert.Equal(expected, SearchEngine.MatchesWildcard(input, pattern, caseSensitive));
    }
}
