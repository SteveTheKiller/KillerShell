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

        // "a+c / b+d" reads as one band of two columns: a then c down the left, b then d down the right.
        private static string ShowBands(IEnumerable<List<string>[]> bands)
            => string.Join(" | ", bands.Select(band => string.Join(" / ", band.Select(column => string.Join("+", column)))));

        [Fact]
        public void Bands_FlowSlotsDownTwoColumnsInOrder()
        {
            // Each column runs on its own, so a card sits right under the one above it in its
            // column instead of waiting for a taller card in the other column to end.
            var bands = Bands(new[] { "a", "b", "c", "d", "e" }, _ => false);
            Assert.Equal("a+c+e / b+d", ShowBands(bands));
        }

        // The panels as the layout would show them: each slot in its remembered column, or
        // taking turns when it has none.
        private static string Picture(DashboardLayout layout, IEnumerable<string> present)
            => ShowBands(Bands(layout.Slots(Panels, present), slot => layout.IsWide(Panels, slot[0]),
                slot => layout.Column(Panels, slot[0]))
                .Select(band => band.Select(column => column.Select(slot => string.Join("&", slot)).ToList()).ToArray()));

        private static readonly string[] Five = ["a", "b", "c", "d", "e"];

        // a, c and e down the left; b and d down the right: how five cards first come up.
        private static Dictionary<string, int> Shown(DashboardLayout layout)
            => Bands(layout.Slots(Panels, Five), _ => false, slot => layout.Column(Panels, slot[0]))
                .SelectMany(band => band.Select((column, c) => (column, c)))
                .SelectMany(pair => pair.column.SelectMany(slot => slot).Select(id => (id, pair.c)))
                .ToDictionary(pair => pair.id, pair => pair.c);

        [Fact]
        public void MoveInColumn_DroppedUnderTheLastCardOfAColumnLandsThere()
        {
            var layout = Parse(null);
            Assert.Equal("a+c+e / b+d", Picture(layout, Five));

            layout.MoveInColumn(Panels, layout.Slots(Panels, Five), Shown(layout), "a", "d", Drop.After);

            // Only a moves: c and e stay down the left, and a joins b and d on the right.
            Assert.Equal("c+e / b+d+a", Picture(layout, Five));
        }

        [Fact]
        public void MoveInColumn_DroppedAboveACardTakesItsPlaceInItsColumn()
        {
            var layout = Parse(null);
            layout.MoveInColumn(Panels, layout.Slots(Panels, Five), Shown(layout), "a", "d", Drop.Before);
            Assert.Equal("c+e / b+a+d", Picture(layout, Five));

            layout.MoveInColumn(Panels, layout.Slots(Panels, Five), Shown(layout), "e", "c", Drop.Before);
            Assert.Equal("e+c / b+a+d", Picture(layout, Five));
        }

        [Fact]
        public void MoveInColumn_ColumnsSurviveARestart()
        {
            var layout = Parse(null);
            layout.MoveInColumn(Panels, layout.Slots(Panels, Five), Shown(layout), "a", "d", Drop.After);

            var reread = Parse(layout.Serialize());

            Assert.Equal("c+e / b+d+a", Picture(reread, Five));
            reread.Reset();
            Assert.True(reread.IsDefault);
            Assert.Equal("a+c+e / b+d", Picture(reread, Five));
        }

        [Fact]
        public void MoveInColumn_AColumnEmptiedByDragsTakesACardBack()
        {
            var layout = Parse(null);
            string[] four = ["a", "b", "c", "d"];
            Dictionary<string, int> Now() => Bands(layout.Slots(Panels, four), _ => false, slot => layout.Column(Panels, slot[0]))
                .SelectMany(band => band.Select((column, c) => (column, c)))
                .SelectMany(pair => pair.column.SelectMany(slot => slot).Select(id => (id, pair.c)))
                .ToDictionary(pair => pair.id, pair => pair.c);

            layout.MoveInColumn(Panels, layout.Slots(Panels, four), Now(), "b", "c", Drop.After);
            layout.MoveInColumn(Panels, layout.Slots(Panels, four), Now(), "d", "b", Drop.After);
            Assert.Equal("a+c+b+d / ", Picture(layout, four));

            // Dropped into the empty right column, placed after the last card of the band.
            layout.MoveInColumn(Panels, layout.Slots(Panels, four), Now(), "b", "d", Drop.After, column: 1);
            Assert.Equal("a+c+d / b", Picture(layout, four));
        }

        [Fact]
        public void MoveInColumn_ANewCardJoinsTheShorterColumn()
        {
            var layout = Parse(null);
            layout.MoveInColumn(Panels, layout.Slots(Panels, Five), Shown(layout), "a", "d", Drop.After);

            // Left holds c and e, right holds b, d and a, so a sixth card goes left.
            Assert.Equal("c+e+f / b+d+a", Picture(layout, [.. Five, "f"]));
        }

        [Fact]
        public void Bands_FlowSlotsDownThreeColumnsOnAWidePage()
        {
            var bands = Bands(new[] { "a", "b", "c", "d", "e" }, _ => false, count: 3);
            Assert.Equal("a+d / b+e / c", ShowBands(bands));
        }

        [Fact]
        public void MoveInColumn_TheThirdColumnSurvivesARestartAndFoldsRightOnTwo()
        {
            var layout = Parse(null);
            var three = Bands(layout.Slots(Panels, Five), _ => false, slot => layout.Column(Panels, slot[0]), count: 3)
                .SelectMany(band => band.Select((column, c) => (column, c)))
                .SelectMany(pair => pair.column.SelectMany(slot => slot).Select(id => (id, pair.c)))
                .ToDictionary(pair => pair.id, pair => pair.c);

            // a under c, at the bottom of the third column.
            layout.MoveInColumn(Panels, layout.Slots(Panels, Five), three, "a", "c", Drop.After);
            var reread = Parse(layout.Serialize());
            Assert.Equal(2, reread.Column(Panels, "a"));
            Assert.Equal("d / b+e / c+a", ShowBands(Bands(reread.Slots(Panels, Five), _ => false,
                slot => reread.Column(Panels, slot[0]), count: 3)
                .Select(band => band.Select(column => column.Select(slot => string.Join("&", slot)).ToList()).ToArray())));

            // On a two-column page the third column's cards go down the right one.
            Assert.Equal("d / b+c+a+e", Picture(reread, Five));

            reread.Reset();
            Assert.True(reread.IsDefault);
        }

        [Fact]
        public void Bands_GiveATwoColumnSlotABandToItself()
        {
            // "a" cannot pair with the wide slot after it, so it keeps the left column of its band.
            var bands = Bands(new[] { "a", "B", "c", "d", "e" }, slot => slot == "B");
            Assert.Equal("a /  | B | c+e / d", ShowBands(bands));
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
