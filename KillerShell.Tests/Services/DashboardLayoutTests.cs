using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class DashboardLayoutTests
    {
        private static readonly string[] Designed = ["cpu", "ram", "disks", "network"];

        [Fact]
        public void Arrange_WithNothingSavedKeepsTheDesignedOrder()
        {
            var layout = DashboardLayout.Parse(null);
            Assert.Equal(Designed, layout.Arrange(DashboardLayout.Panels, Designed));
            Assert.True(layout.IsDefault);
        }

        [Fact]
        public void Move_PutsACardBeforeOrAfterAnother()
        {
            var layout = DashboardLayout.Parse(null);

            var order = layout.Move(DashboardLayout.Panels, Designed, "disks", "cpu", after: false);
            Assert.Equal(new[] { "disks", "cpu", "ram", "network" }, order);

            order = layout.Move(DashboardLayout.Panels, order, "cpu", "network", after: true);
            Assert.Equal(new[] { "disks", "ram", "network", "cpu" }, order);

            Assert.Equal(order, layout.Arrange(DashboardLayout.Panels, Designed));
            Assert.Equal(Designed, layout.Arrange(DashboardLayout.Strip, Designed));
        }

        [Fact]
        public void Reordered_ShowsWhetherADropChangesAnything()
        {
            // The right half of the card before it is where the card already is.
            Assert.Equal(Designed, DashboardLayout.Reordered(Designed, "ram", "cpu", after: true));
            Assert.Equal(new[] { "ram", "cpu", "disks", "network" }, DashboardLayout.Reordered(Designed, "ram", "cpu", after: false));
        }

        [Fact]
        public void Move_ThatEndsWhereItStartedRecordsNothing()
        {
            var layout = DashboardLayout.Parse(null);
            Assert.Equal(Designed, layout.Move(DashboardLayout.Panels, Designed, "ram", "cpu", after: true));
            Assert.True(layout.IsDefault);
        }

        [Fact]
        public void Move_OntoItselfChangesNothing()
        {
            var layout = DashboardLayout.Parse(null);
            Assert.Equal(Designed, layout.Move(DashboardLayout.Panels, Designed, "ram", "ram", after: true));
            Assert.True(layout.IsDefault);
        }

        [Fact]
        public void Arrange_SkipsCardsThatAreGoneAndAddsNewOnesAtTheEnd()
        {
            var layout = DashboardLayout.Parse(null);
            layout.Move(DashboardLayout.Panels, ["cpu", "ram", "network", "gpu:A"], "network", "cpu", after: false);

            Assert.Equal(new[] { "network", "cpu", "ram", "gpu:B" },
                layout.Arrange(DashboardLayout.Panels, ["cpu", "ram", "network", "gpu:B"]));
        }

        [Fact]
        public void Move_KeepsTheSavedPlaceOfACardThatIsGoneForNow()
        {
            var layout = DashboardLayout.Parse(null);
            layout.Move(DashboardLayout.Panels, ["cpu", "gpu:A", "ram"], "gpu:A", "cpu", after: false);

            // The adapter is unplugged, and the remaining cards are rearranged meanwhile.
            layout.Move(DashboardLayout.Panels, ["cpu", "ram"], "ram", "cpu", after: false);

            Assert.Equal(new[] { "gpu:A", "ram", "cpu" }, layout.Arrange(DashboardLayout.Panels, ["cpu", "ram", "gpu:A"]));
        }

        [Fact]
        public void Folded_IsRememberedPerArea()
        {
            var layout = DashboardLayout.Parse(null);
            layout.SetFolded(DashboardLayout.Panels, "disks", true);

            Assert.True(layout.IsFolded(DashboardLayout.Panels, "disks"));
            Assert.False(layout.IsFolded(DashboardLayout.Strip, "disks"));
            layout.SetFolded(DashboardLayout.Panels, "disks", false);
            Assert.True(layout.IsDefault);
        }

        [Fact]
        public void Layout_SurvivesItsSettingWithAwkwardAdapterNames()
        {
            var layout = DashboardLayout.Parse(null);
            string[] cards = ["cpu", "gpu:Vendor, Inc. GPU; rev=2/a%b", "ram"];
            layout.Move(DashboardLayout.Panels, cards, "ram", "cpu", after: false);
            layout.Move(DashboardLayout.Strip, Designed, "network", "cpu", after: false);
            layout.SetFolded(DashboardLayout.Panels, cards[1], true);

            var reread = DashboardLayout.Parse(layout.Serialize());

            Assert.Equal(new[] { "ram", "cpu", cards[1] }, reread.Arrange(DashboardLayout.Panels, cards));
            Assert.Equal(new[] { "network", "cpu", "ram", "disks" }, reread.Arrange(DashboardLayout.Strip, Designed));
            Assert.True(reread.IsFolded(DashboardLayout.Panels, cards[1]));
        }

        [Theory]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("order.panels=;folded=nowhere")]
        [InlineData(";;=,,%ZZ")]
        public void Parse_TreatsAMalformedSettingAsTheDefault(string saved)
        {
            var layout = DashboardLayout.Parse(saved);
            Assert.Equal(Designed, layout.Arrange(DashboardLayout.Panels, Designed));
        }

        [Fact]
        public void Reset_ForgetsTheOrderAndTheFolds()
        {
            var layout = DashboardLayout.Parse(null);
            layout.Move(DashboardLayout.Panels, Designed, "network", "cpu", after: false);
            layout.SetFolded(DashboardLayout.Strip, "cpu", true);

            layout.Reset();

            Assert.True(layout.IsDefault);
            Assert.Equal(Designed, layout.Arrange(DashboardLayout.Panels, Designed));
            Assert.False(layout.IsFolded(DashboardLayout.Strip, "cpu"));
        }

        [Fact]
        public void Rows_PairsCardsTwoToARow()
        {
            List<string[]> rows = DashboardLayout.Rows(["a", "b", "c", "d", "e"]);
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "a", "b" }, rows[0]);
            Assert.Equal(new[] { "c", "d" }, rows[1]);
            Assert.Equal(new[] { "e" }, rows[2]);
        }
    }
}
