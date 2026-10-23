using System.Collections.Generic;
using System.Linq;
using KillerShell.Services;
using Xunit;
using static KillerShell.Services.DashboardLayout;

namespace KillerShell.Tests.Services
{
    public sealed class DashboardLayoutTests
    {
        private static readonly string[] Designed = ["cpu", "ram", "disks", "network"];

        // "cpu+ram|disks" reads as two slots: cpu with ram stacked under it, then disks.
        private static string Show(IEnumerable<IEnumerable<string>> slots)
            => string.Join("|", slots.Select(slot => string.Join("+", slot)));

        private static List<List<string>> Slotted(string shown)
            => shown.Split('|').Select(slot => slot.Split('+').ToList()).ToList();

        [Fact]
        public void Slots_WithNothingSavedAreTheDesignedOrderOneCardEach()
        {
            var layout = Parse(null);
            Assert.Equal("cpu|ram|disks|network", Show(layout.Slots(Panels, Designed)));
            Assert.True(layout.IsDefault);
        }

        [Fact]
        public void Move_PutsACardBeforeOrAfterAnother()
        {
            var layout = Parse(null);

            var slots = layout.Move(Panels, Slotted("cpu|ram|disks|network"), "disks", "cpu", Drop.Before);
            Assert.Equal("disks|cpu|ram|network", Show(slots));

            slots = layout.Move(Panels, slots, "cpu", "network", Drop.After);
            Assert.Equal("disks|ram|network|cpu", Show(slots));

            Assert.Equal("disks|ram|network|cpu", Show(layout.Slots(Panels, Designed)));
            Assert.Equal("cpu|ram|disks|network", Show(layout.Slots(Strip, Designed)));
        }

        [Fact]
        public void Move_BelowStacksACardInTheGapUnderAnother()
        {
            var layout = Parse(null);
            var slots = layout.Move(Panels, Slotted("cpu|disks|ram|network"), "ram", "cpu", Drop.Below);

            Assert.Equal("cpu+ram|disks|network", Show(slots));
            Assert.Equal("cpu+ram|disks|network", Show(layout.Slots(Panels, Designed)));
        }

        [Fact]
        public void Move_BesideTakesACardOutOfItsStack()
        {
            var layout = Parse(null);
            var slots = layout.Move(Panels, Slotted("cpu+ram|disks|network"), "ram", "network", Drop.After);
            Assert.Equal("cpu|disks|network|ram", Show(slots));
        }

        [Fact]
        public void Move_TakingTheTopCardLeavesTheRestOfItsStackInPlace()
        {
            var layout = Parse(null);
            var slots = layout.Move(Panels, Slotted("cpu+ram+gpu|disks"), "cpu", "disks", Drop.After);

            Assert.Equal("ram+gpu|disks|cpu", Show(slots));
            Assert.Equal("ram+gpu|disks|cpu", Show(layout.Slots(Panels, ["cpu", "ram", "gpu", "disks"])));
        }

        [Fact]
        public void Move_ThatEndsWhereItStartedRecordsNothing()
        {
            var layout = Parse(null);
            var designed = Slotted("cpu|ram|disks|network");

            // The right half of the card before it, its own card, and the gap it already ends.
            Assert.Equal("cpu|ram|disks|network", Show(layout.Move(Panels, designed, "ram", "cpu", Drop.After)));
            Assert.Equal("cpu|ram|disks|network", Show(layout.Move(Panels, designed, "ram", "ram", Drop.Before)));
            Assert.Equal("cpu+ram|disks", Show(layout.Move(Panels, Slotted("cpu+ram|disks"), "ram", "ram", Drop.Below)));
            Assert.True(layout.IsDefault);
        }

        [Fact]
        public void Rearranged_ShowsWhetherADropChangesAnything()
        {
            var shown = Slotted("cpu|ram|disks");
            Assert.True(Same(shown, Rearranged(shown, "ram", "cpu", Drop.After)));
            Assert.False(Same(shown, Rearranged(shown, "ram", "cpu", Drop.Before)));
            Assert.False(Same(shown, Rearranged(shown, "ram", "cpu", Drop.Below)));
        }

        [Fact]
        public void Slots_SkipCardsThatAreGoneAndAddNewOnesAtTheEnd()
        {
            var layout = Parse(null);
            layout.Move(Panels, Slotted("cpu|ram|network|gpu:A"), "network", "cpu", Drop.Before);

            Assert.Equal("network|cpu|ram|gpu:B", Show(layout.Slots(Panels, ["cpu", "ram", "network", "gpu:B"])));
        }

        [Fact]
        public void Slots_ACardStackedUnderOneThatIsGoneStandsAlone()
        {
            var layout = Parse(null);
            layout.Move(Panels, Slotted("cpu|gpu:A|ram"), "ram", "gpu:A", Drop.Below);

            Assert.Equal("cpu|ram", Show(layout.Slots(Panels, ["cpu", "ram"])));
            Assert.Equal("cpu|gpu:A+ram", Show(layout.Slots(Panels, ["cpu", "ram", "gpu:A"])));
        }

        [Fact]
        public void Move_KeepsTheSavedPlaceOfACardThatIsGoneForNow()
        {
            var layout = Parse(null);
            layout.Move(Panels, Slotted("cpu|gpu:A|ram"), "gpu:A", "cpu", Drop.Before);

            // The adapter is unplugged, and the remaining cards are rearranged meanwhile.
            layout.Move(Panels, Slotted("cpu|ram"), "ram", "cpu", Drop.Before);

            Assert.Equal("gpu:A|ram|cpu", Show(layout.Slots(Panels, ["cpu", "ram", "gpu:A"])));
        }

        [Fact]
        public void Folded_IsRememberedPerArea()
        {
            var layout = Parse(null);
            layout.SetFolded(Panels, "disks", true);

            Assert.True(layout.IsFolded(Panels, "disks"));
            Assert.False(layout.IsFolded(Strip, "disks"));
            layout.SetFolded(Panels, "disks", false);
            Assert.True(layout.IsDefault);
        }

        [Fact]
        public void Layout_SurvivesItsSettingWithAwkwardAdapterNames()
        {
            var layout = Parse(null);
            string odd = "gpu:Vendor, Inc. GPU; rev=2/a%b+c|d";
            layout.Move(Panels, [["cpu"], [odd], ["ram"]], "ram", "cpu", Drop.Before);
            layout.Move(Panels, [["ram"], ["cpu"], [odd]], odd, "ram", Drop.Below);
            layout.Move(Strip, Slotted("cpu|ram|disks|network"), "network", "cpu", Drop.Before);
            layout.SetFolded(Panels, odd, true);

            var reread = Parse(layout.Serialize());

            var slots = reread.Slots(Panels, ["cpu", odd, "ram"]);
            Assert.Equal(2, slots.Count);
            Assert.Equal(new[] { "ram", odd }, slots[0]);
            Assert.Equal(new[] { "cpu" }, slots[1]);
            Assert.Equal("network|cpu|ram|disks", Show(reread.Slots(Strip, Designed)));
            Assert.True(reread.IsFolded(Panels, odd));
        }

        [Theory]
        [InlineData("")]
        [InlineData("garbage")]
        [InlineData("order.panels=;folded=nowhere;stacked=x")]
        [InlineData(";;=,,%ZZ")]
        public void Parse_TreatsAMalformedSettingAsTheDefault(string saved)
        {
            var layout = Parse(saved);
            Assert.Equal("cpu|ram|disks|network", Show(layout.Slots(Panels, Designed)));
        }

        [Fact]
        public void Reset_ForgetsTheOrderTheStacksAndTheFolds()
        {
            var layout = Parse(null);
            layout.Move(Panels, Slotted("cpu|ram|disks|network"), "network", "cpu", Drop.Below);
            layout.SetFolded(Strip, "cpu", true);

            layout.Reset();

            Assert.True(layout.IsDefault);
            Assert.Equal("cpu|ram|disks|network", Show(layout.Slots(Panels, Designed)));
            Assert.False(layout.IsFolded(Strip, "cpu"));
        }

        [Fact]
        public void Rows_PairsSlotsTwoToARow()
        {
            List<string[]> rows = Rows(new[] { "a", "b", "c", "d", "e" }, _ => false);
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "a", "b" }, rows[0]);
            Assert.Equal(new[] { "c", "d" }, rows[1]);
            Assert.Equal(new[] { "e" }, rows[2]);
        }

        [Fact]
        public void Rows_GiveATwoColumnSlotItsRowToItself()
        {
            // "a" cannot pair with the wide slot after it, so it keeps one column of its own row.
            List<string[]> rows = Rows(new[] { "a", "B", "c", "d", "e" }, slot => slot == "B");
            Assert.Equal("a|B|c+d|e", Show(rows));
        }

        [Fact]
        public void Wide_IsRememberedPerCardAndReset()
        {
            var layout = Parse(null);
            layout.SetWide(Panels, "gpu:A", true);
            Assert.True(layout.IsWide(Panels, "gpu:A"));
            Assert.False(layout.IsWide(Strip, "gpu:A"));

            var reread = Parse(layout.Serialize());
            Assert.True(reread.IsWide(Panels, "gpu:A"));

            reread.SetWide(Panels, "gpu:A", false);
            Assert.True(reread.IsDefault);
            layout.Reset();
            Assert.False(layout.IsWide(Panels, "gpu:A"));
        }
    }
}
