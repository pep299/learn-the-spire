using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using Lts2.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Lts2.Harness.Tests;

/// <summary>
/// Exercises the Crystal Sphere event minigame end-to-end through the public option API. The game
/// drives this minigame through a UI screen (null headless); the harness skips that screen and
/// surfaces the grid as <see cref="GamePhase.CrystalSphere"/> with one
/// <see cref="OptionKind.ClickCrystalSphereCell"/> per hidden cell plus a tool toggle. These tests
/// verify the public grid boundary and completion without exposing hidden item placements.
/// </summary>
public sealed class CrystalSphereTests
{
    private readonly ITestOutputHelper _out;

    public CrystalSphereTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void PublicGrid_HidesPlacements_AndTracksRealToolClicks()
    {
        GameHost host = TestNav.StartOnMap("TESTSEED");
        EnterMinigameViaPaymentPlan(host);
        CrystalSphereView initial = host.GetState().CrystalSphere!;
        Assert.Empty(initial.Items);
        Assert.Equal(initial.Width * initial.Height, initial.HiddenCells.Count + initial.RevealedCells.Count);
        int remaining = initial.DivinationsLeft;
        host.Apply(host.ListOptions().Single(o =>
            o.Kind == OptionKind.SetCrystalSphereTool && o.CrystalSphereTool == "Small"));
        Assert.Equal(remaining, host.GetState().CrystalSphere!.DivinationsLeft);
        Coord cell = initial.HiddenCells[0];
        ClickCell(host, cell);
        CrystalSphereView after = host.GetState().CrystalSphere!;
        Assert.Empty(after.Items);
        Assert.Equal(remaining - 1, after.DivinationsLeft);
        Assert.Equal(initial.RevealedCells.Count + 1, after.RevealedCells.Count);
        Assert.Contains(cell, after.RevealedCells);
        Assert.DoesNotContain(cell, after.HiddenCells);
        Assert.DoesNotContain(host.ListOptions(), o => o.CrystalSphereCell == cell);
        host.Apply(host.ListOptions().Single(o =>
            o.Kind == OptionKind.SetCrystalSphereTool && o.CrystalSphereTool == "Big"));
        Coord center = after.HiddenCells.First(c => c.Col > 0 && c.Row > 0 && c.Col < after.Width - 1 && c.Row < after.Height - 1);
        ClickCell(host, center);
        CrystalSphereView big = host.GetState().CrystalSphere!;
        Assert.True(big.RevealedCells.Count > after.RevealedCells.Count + 1);
        Assert.Empty(big.Items);
        Assert.Equal(big.Width * big.Height, big.HiddenCells.Count + big.RevealedCells.Count);
    }

    [Fact]
    public void PlayingTheMinigameOut_GrantsRewardsForRevealedItems_AndReturnsToTheMap()
    {
        GameHost host = TestNav.StartOnMap("TESTSEED");
        int relicsBefore = host.GetState().Players[0].Relics.Count;
        int deckBefore = host.GetState().Players[0].Deck.Count;
        EnterMinigameViaPaymentPlan(host);

        // Greedily spend every divination uncovering items, then take the rewards the revealed items
        // pay out (which surface through the normal custom-reward screen) until back on the map.
        GameState end = AutoPlayer.Advance(
            host,
            stop: s => s.Phase == GamePhase.Map || s.Phase == GamePhase.GameOver,
            maxSteps: 200,
            log: _out);

        PlayerState player = end.Players[0];
        _out.WriteLine($"ended phase={end.Phase} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck.Count}");

        Assert.Equal(GamePhase.Map, end.Phase);
        Assert.Null(end.CrystalSphere);
        // PaymentPlan adds a curse to the deck, so the deck must have grown (plus any card the greedy
        // play uncovered and took) — evidence the minigame resolved and paid out through to the map.
        Assert.True(player.Deck.Count > deckBefore,
            $"expected the deck to grow (curse + any revealed card) but it went {deckBefore} -> {player.Deck.Count}");
        Assert.True(player.Relics.Count >= relicsBefore, "relic count should not regress through the minigame");
    }

    [Fact]
    public void PublicMinigame_AllowsAnyTimePotionAndDiscard_WithoutSpendingDivinations()
    {
        using var errors = new LogErrorSink();
        var host = TestNav.StartOnMap("TESTSEED");
        TestNav.SetHp(host, maxHp: 80, currentHp: 40);
        TestNav.GivePotion(host, "BloodPotion");
        TestNav.GivePotion(host, "BlockPotion");
        EnterMinigameViaPaymentPlan(host);
        var before = host.GetState().CrystalSphere!;
        Assert.DoesNotContain(host.ListOptions(), o => o.Kind == OptionKind.UsePotion && o.PotionId == "BLOCK_POTION");
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.UsePotion && o.PotionId == "BLOOD_POTION"));
        var healed = host.GetState();
        Assert.Equal(56, healed.Players[0].CurrentHp);
        Assert.DoesNotContain("BLOOD_POTION", healed.Players[0].Potions);
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.DiscardPotion && o.PotionId == "BLOCK_POTION"));
        var after = host.GetState();
        Assert.DoesNotContain("BLOCK_POTION", after.Players[0].Potions);
        Assert.Equal(GamePhase.CrystalSphere, after.Phase);
        Assert.Equal(before.DivinationsLeft, after.CrystalSphere!.DivinationsLeft);
        Assert.Equal(before.HiddenCells, after.CrystalSphere.HiddenCells);
        Assert.Equal(before.RevealedCells, after.CrystalSphere.RevealedCells);
        Assert.Empty(errors.Errors);
    }

    /// <summary>
    /// Resolve the run to the Crystal Sphere event's PaymentPlan option, which (unlike UncoverFuture)
    /// needs no gold — it adds a Debt curse — and then opens the minigame.
    /// </summary>
    private void EnterMinigameViaPaymentPlan(GameHost host)
    {
        EventModel ev = Act1EventsTests.ResolveEvent("CrystalSphere");
        host.EnterEventDebug(ev);

        GameState atEvent = host.GetState();
        Assert.Equal(GamePhase.Event, atEvent.Phase);
        int paymentPlanIndex = atEvent.Event!.Options
            .Single(o => o.TextKey.Contains("PAYMENT_PLAN")).Index;

        host.Apply(host.ListOptions().Single(o =>
            o.Kind == OptionKind.ChooseEventOption && o.EventOptionIndex == paymentPlanIndex));

        Assert.Equal(GamePhase.CrystalSphere, host.GetState().Phase);
    }

    private static void ClickCell(GameHost host, Coord cell) =>
        host.Apply(host.ListOptions().Single(o =>
            o.Kind == OptionKind.ClickCrystalSphereCell && o.CrystalSphereCell == cell));
}
