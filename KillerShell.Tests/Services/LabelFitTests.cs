using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class LabelFitTests
    {
        // Ten pixels a character at 18 points, in proportion at other sizes.
        private static double Width(string text, double size) => text.Length * 10 * size / 18;

        [Fact]
        public void TheFullNameKeepsItsSizeWhileItFits()
            => Assert.Equal(("NETWORK", 18.0), LabelFit.Choose(70, "NETWORK", "NET", Width));

        [Fact]
        public void TheFullNameShrinksALittleBeforeItIsShortened()
            => Assert.Equal(("NETWORK", 16.0), LabelFit.Choose(63, "NETWORK", "NET", Width));

        [Fact]
        public void TheShortNameTakesOverAtFullSizeWhenTheFullOneWillNotFit()
            => Assert.Equal(("NET", 18.0), LabelFit.Choose(40, "NETWORK", "NET", Width));

        [Fact]
        public void TheShortNameShrinksTooBeforeTheNameIsDropped()
            => Assert.Equal(("NET", 16.0), LabelFit.Choose(27, "NETWORK", "NET", Width));

        [Fact]
        public void NothingIsShownWhereEvenTheShortNameWillNotFit()
            => Assert.Null(LabelFit.Choose(20, "NETWORK", "NET", Width));

        [Fact]
        public void ANameWithNoShortFormShrinksAndThenGoes()
        {
            Assert.Equal(("CPU", 15.0), LabelFit.Choose(25, "CPU", "CPU", Width));
            Assert.Null(LabelFit.Choose(24, "CPU", "", Width));
        }
    }
}
