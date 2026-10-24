using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class SummaryFitTests
    {
        // A reading 60 wide at full size beside a 28 icon and an 8 gap: 96 in all.
        [Fact]
        public void EverythingKeepsItsSizeWhileItFits()
            => Assert.Equal((28.0, 20.0), SummaryFit.Choose(96, 60));

        [Fact]
        public void TheIconShrinksFirst()
            => Assert.Equal((20.0, 20.0), SummaryFit.Choose(88, 60));

        [Fact]
        public void TheIconGoesBeforeTheNumberShrinks()
        {
            Assert.Equal((0.0, 20.0), SummaryFit.Choose(83, 60));
            Assert.Equal((0.0, 20.0), SummaryFit.Choose(60, 60));
        }

        [Fact]
        public void TheNumberShrinksOnlyOnceTheIconIsGone()
            => Assert.Equal((0.0, 15.0), SummaryFit.Choose(45, 60));

        [Fact]
        public void TheNumberStopsAtItsSmallestSize()
            => Assert.Equal((0.0, 11.0), SummaryFit.Choose(20, 60));
    }
}
