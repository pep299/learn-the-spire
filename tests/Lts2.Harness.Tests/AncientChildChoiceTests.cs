using System.Linq;
using Lts2.Harness;
using MegaCrit.Sts2.Core.Models;
using Xunit;

namespace Lts2.Harness.Tests;

public sealed class AncientChildChoiceTests
{
    private static RelicModel Relic(string name)
    {
        var relic = ModelDb.AllRelics.Single(r => r.GetType().Name == name);
        if (relic is MegaCrit.Sts2.Core.Models.Relics.SeaGlass)
        {
            var glass = (MegaCrit.Sts2.Core.Models.Relics.SeaGlass)relic.ToMutable();
            glass.CharacterId = ModelDb.Character<MegaCrit.Sts2.Core.Models.Characters.Silent>().Id;
            return glass;
        }
        return relic;
    }

    [Theory]
    [InlineData("Claws", 0, 6, 0)]
    [InlineData("Claws", 6, 6, 0)]
    [InlineData("SeaGlass", 0, 15, 0)]
    [InlineData("SeaGlass", 15, 15, 0)]
    [InlineData("HeftyTablet", 0, 1, 1)]
    [InlineData("HeftyTablet", 1, 1, 1)]
    [InlineData("LeadPaperweight", 0, 1, 0)]
    [InlineData("LeadPaperweight", 1, 1, 0)]
    public void OptionalGrid_ExplicitEmptyAndMaximumSelections_RunRealEffect(
        string relic, int count, int maximum, int extraCards)
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        var before = host.Run.Players[0].Deck.Cards.Select(c => c.Id.Entry).Order().ToArray();
        host.ObtainRelicDebug(Relic(relic));
        Assert.Equal(GamePhase.Choice, host.GetState().Phase);
        var choice = host.GetState().PendingChoice!;
        Assert.Equal(0, choice.MinSelect);
        Assert.Equal(maximum, choice.MaxSelect);
        Assert.True(choice.Options.Count >= maximum);
        var indices = Enumerable.Range(0, choice.Options.Count).Reverse().Take(count).ToArray();
        var chosenIds = indices.Select(i => choice.Options[i].CardId).ToArray();
        host.ApplyCardChoice(indices);
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
        var after = host.Run.Players[0].Deck.Cards.Select(c => c.Id.Entry).Order().ToArray();
        if (relic == "Claws")
        {
            Assert.Equal(before.Length, after.Length);
            Assert.Equal(count, after.Count(id => id == "MAUL"));
            if (count == 0) Assert.Equal(before, after);
        }
        else
        {
            Assert.Equal(before.Length + count + extraCards, after.Length);
            foreach (var group in chosenIds.GroupBy(id => id))
                Assert.True(after.Count(id => id == group.Key) >= before.Count(id => id == group.Key) + group.Count());
        }
        Assert.Empty(errors.Errors);
    }

    [Theory]
    [InlineData("BiiigHug", 4)]
    [InlineData("EmptyCage", 2)]
    [InlineData("PaelsTooth", 5)]
    public void Removal_ExplicitNonPrefixSelection_RemovesExactlyChosenCards(string relic, int count)
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        int before = host.Run.Players[0].Deck.Cards.Count;
        host.ObtainRelicDebug(Relic(relic));
        var choice = host.GetState().PendingChoice!;
        Assert.Equal(count, choice.MinSelect);
        Assert.Equal(count, choice.MaxSelect);
        var indices = Enumerable.Range(0, choice.Options.Count).Reverse().Take(count).ToArray();
        var selected = indices.Select(i => choice.Options[i].CardId).GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());
        var counts = host.Run.Players[0].Deck.Cards.GroupBy(c => c.Id.Entry)
            .ToDictionary(group => group.Key, group => group.Count());
        host.ApplyCardChoice(indices);
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
        var after = host.Run.Players[0].Deck.Cards;
        Assert.Equal(before - count, after.Count);
        foreach (var pair in counts)
            Assert.Equal(pair.Value - selected.GetValueOrDefault(pair.Key), after.Count(c => c.Id.Entry == pair.Key));
        Assert.Empty(errors.Errors);
    }

    [Theory]
    [InlineData("Pomander", 1)]
    [InlineData("YummyCookie", 4)]
    public void Upgrade_ExplicitSelection_UpgradesExactlyChosenCards(string relic, int count)
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        host.ObtainRelicDebug(Relic(relic));
        var choice = host.GetState().PendingChoice!;
        Assert.Equal(count, choice.MinSelect);
        Assert.Equal(count, choice.MaxSelect);
        var indices = Enumerable.Range(0, choice.Options.Count).Reverse().Take(count).ToArray();
        int upgradesBefore = host.Run.Players[0].Deck.Cards.Sum(c => c.CurrentUpgradeLevel);
        host.ApplyCardChoice(indices);
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
        Assert.Equal(upgradesBefore + count, host.Run.Players[0].Deck.Cards.Sum(c => c.CurrentUpgradeLevel));
        Assert.Empty(errors.Errors);
    }

    [Fact]
    public void PaelsGrowth_ExplicitSelection_AppliesCloneEnchantment()
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        host.ObtainRelicDebug(Relic("PaelsGrowth"));
        var choice = host.GetState().PendingChoice!;
        Assert.Equal(1, choice.MinSelect);
        Assert.Equal(1, choice.MaxSelect);
        host.ApplyCardChoice(new[] { choice.Options.Count - 1 });
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
        Assert.Single(host.GetState().Players[0].Deck, c => c.EnchantmentId == "CLONE");
        Assert.Empty(errors.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ScrollBoxes_BothBundleIndices_AddExactlyThatBundle(int index)
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        var before = host.Run.Players[0].Deck.Cards.Select(c => c.Id.Entry).ToArray();
        host.ObtainRelicDebug(Relic("ScrollBoxes"));
        var options = host.ListOptions();
        Assert.Equal(2, options.Count);
        var pick = options.Single(o => o.Kind == OptionKind.ChooseBundle && o.BundleIndex == index);
        var expected = before.Concat(pick.BundleCards!.Select(c => c.CardId)).Order().ToArray();
        host.Apply(pick);
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
        Assert.Equal(expected, host.Run.Players[0].Deck.Cards.Select(c => c.Id.Entry).Order().ToArray());
        Assert.Empty(errors.Errors);
    }
}
