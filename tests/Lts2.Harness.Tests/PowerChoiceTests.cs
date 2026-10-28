using System.Linq;
using Lts2.Harness;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using Xunit;

namespace Lts2.Harness.Tests;

public sealed class PowerChoiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForegoneConclusion_WithoutLocalization_OffersChoiceAndResumesTurn(bool upgraded)
    {
        GameHost host = TestNav.MoveIntoFirstCombat("TESTSEED");
        var player = host.Run.Players[0];
        var combat = host.Combat!;
        var pcs = player.PlayerCombatState!;
        ForegoneConclusion card = combat.CreateCard<ForegoneConclusion>(player);
        if (upgraded) card.UpgradeInternal();
        pcs.Hand.AddInternal(card);

        Assert.True(host.PlayCard(card, target: null));
        int count = player.Creature.Powers.OfType<ForegoneConclusionPower>().Single().Amount;
        var drawBefore = pcs.DrawPile.Cards.ToArray();
        Assert.True(drawBefore.Length > count);
        int roundBefore = combat.RoundNumber;

        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.EndTurn));

        GameState choice = host.GetState();
        var options = host.ListOptions();
        host.ApplyCardChoice(Enumerable.Range(0, count).ToArray());

        Assert.Equal(GamePhase.Choice, choice.Phase);
        Assert.NotNull(choice.PendingChoice);
        Assert.Equal(count, choice.PendingChoice.MinSelect);
        Assert.Equal(count, choice.PendingChoice.MaxSelect);
        Assert.Equal(drawBefore.Select(c => c.Id.Entry).OrderBy(id => id),
            choice.PendingChoice.Options.Select(c => c.CardId).OrderBy(id => id));
        Assert.All(options, o => Assert.Equal(OptionKind.SelectCards, o.Kind));

        GameState after = host.GetState();
        Assert.Equal(GamePhase.Combat, after.Phase);
        Assert.Null(after.PendingChoice);
        Assert.True(combat.RoundNumber > roundBefore);
        foreach (var selected in choice.PendingChoice.Options.Take(count).GroupBy(c => c.CardId))
            Assert.True(pcs.Hand.Cards.Count(c => c.Id.Entry == selected.Key) >= selected.Count());
        Assert.Equal(5 + count, pcs.Hand.Cards.Count);
        Assert.DoesNotContain(player.Creature.Powers, p => p is ForegoneConclusionPower);
        Assert.Contains(host.ListOptions(), o => o.Kind == OptionKind.EndTurn);
    }
}
