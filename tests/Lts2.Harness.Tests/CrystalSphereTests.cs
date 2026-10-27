using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;
using MegaCrit.Sts2.Core.Rewards;
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
        Assert.Equal(initial.Width * initial.Height, initial.HiddenCells.Count + initial.RevealedCells.Count);
        int remaining = initial.DivinationsLeft;
        host.Apply(host.ListOptions().Single(o =>
            o.Kind == OptionKind.SetCrystalSphereTool && o.CrystalSphereTool == "Small"));
        Assert.Equal(remaining, host.GetState().CrystalSphere!.DivinationsLeft);
        Coord cell = initial.HiddenCells[0];
        ClickCell(host, cell);
        CrystalSphereView after = host.GetState().CrystalSphere!;
        Assert.Equal(remaining - 1, after.DivinationsLeft);
        Assert.Equal(initial.RevealedCells.Count + 1, after.RevealedCells.Count);
        Assert.Contains(after.RevealedCells, c => c.Col == cell.Col && c.Row == cell.Row);
        Assert.DoesNotContain(cell, after.HiddenCells);
        Assert.DoesNotContain(host.ListOptions(), o => o.CrystalSphereCell == cell);
        host.Apply(host.ListOptions().Single(o =>
            o.Kind == OptionKind.SetCrystalSphereTool && o.CrystalSphereTool == "Big"));
        Coord center = after.HiddenCells.First(c => c.Col > 0 && c.Row > 0 && c.Col < after.Width - 1 && c.Row < after.Height - 1);
        ClickCell(host, center);
        CrystalSphereView big = host.GetState().CrystalSphere!;
        Assert.True(big.RevealedCells.Count > after.RevealedCells.Count + 1);
        Assert.Equal(big.Width * big.Height, big.HiddenCells.Count + big.RevealedCells.Count);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 30)]
    public void BigGold_PaysExactlyThirtyOnlyWhenFullyUncovered_AndReturnsToTheMap(bool complete, int expectedGold)
    {
        using var errors = new LogErrorSink();
        GameHost host = TestNav.StartOnMap("TESTSEED");
        EnterMinigameViaPaymentPlan(host);
        int goldBefore = host.Run.Players[0].Gold;
        var board = (CrystalSphereMinigame)typeof(GameHost).GetProperty("PendingCrystalSphere",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        // 固定操作を作るfixtureだけが配置を参照する。クリックは公開候補を通す。
        var gold = board.Items.OfType<CrystalSphereGold>().First(item => item.Size.X == 2);
        host.Apply(host.ListOptions().Single(o =>
            o.Kind == OptionKind.SetCrystalSphereTool && o.CrystalSphereTool == "Small"));
        ClickCell(host, new Coord(gold.Position.X, gold.Position.Y));
        Assert.Equal(goldBefore, host.Run.Players[0].Gold);
        if (complete) ClickCell(host, new Coord(gold.Position.X + 1, gold.Position.Y));

        int remaining = host.GetState().CrystalSphere!.DivinationsLeft;
        for (int step = 0; step < remaining; step++)
        {
            Coord empty = host.GetState().CrystalSphere!.HiddenCells
                .First(cell => board.cells[cell.Col, cell.Row].Item is null);
            ClickCell(host, empty);
        }
        if (complete)
        {
            Assert.Equal(GamePhase.Reward, host.GetState().Phase);
            var reward = Assert.Single(host.GetState().Rewards!.Rewards);
            Assert.Equal(RewardType.Gold, reward.Type);
            Assert.Equal(expectedGold, reward.Gold);
        }
        GameState end = AutoPlayer.Advance(
            host,
            stop: s => s.Phase == GamePhase.Map || s.Phase == GamePhase.GameOver,
            maxSteps: 20,
            log: _out);
        Assert.Equal(GamePhase.Map, end.Phase);
        Assert.Null(end.CrystalSphere);
        Assert.Equal(goldBefore + expectedGold, end.Players[0].Gold);
        Assert.Empty(errors.Errors);
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
