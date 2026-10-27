using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using RealItems = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;

namespace Lts2.Harness.Tests;

public sealed class CrystalSphereProjectionTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void HiddenCells_DoNotReadItems_AndPrivateChangesDoNotAffectTheOutput()
    {
        var (host, board) = Enter();
        var cell = new CrystalSphereCell(0, 0);
        cell.SetItem(new RealItems.CrystalSphereCurse());
        board.cells[0, 0] = cell;
        var getter = typeof(CrystalSphereCell).GetProperty(nameof(CrystalSphereCell.Item))!.GetMethod!;
        var harmony = new Harmony("Lts2.Tests.CrystalHiddenItemRead");
        harmony.Patch(getter, prefix: new HarmonyMethod(typeof(CrystalSphereProjectionTests), nameof(RejectHiddenItemRead)));
        try
        {
            Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => getter.Invoke(cell, null)).InnerException);
            string before = JsonSerializer.Serialize(host.GetState().CrystalSphere, Json);
            board.cells[0, 0] = new CrystalSphereCell(0, 0);
            Assert.Equal(before, JsonSerializer.Serialize(host.GetState().CrystalSphere, Json));
        }
        finally { harmony.Unpatch(getter, HarmonyPatchType.Prefix, harmony.Id); }
    }

    private static void RejectHiddenItemRead(CrystalSphereCell __instance)
    {
        if (__instance.IsHidden) throw new InvalidOperationException("未開セルの中身を読んだ");
    }

    [Fact]
    public void RealGameItemVariants_ExposeOnlyCoordinatesAndTheSixContentKinds()
    {
        var (host, board) = Enter();
        var owner = host.Run.Players[0];
        CrystalSphereItem?[] items =
        {
            null, new RealItems.CrystalSphereGold(false), new RealItems.CrystalSphereGold(true),
            new RealItems.CrystalSpherePotion(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Common),
            new RealItems.CrystalSpherePotion(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Uncommon),
            new RealItems.CrystalSpherePotion(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Rare),
            new RealItems.CrystalSphereCardReward(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Common, owner),
            new RealItems.CrystalSphereCardReward(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Uncommon, owner),
            new RealItems.CrystalSphereCardReward(MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Rare, owner),
            new RealItems.CrystalSphereRelic(), new RealItems.CrystalSphereCurse()
        };
        string[] expected = { "empty", "gold", "gold", "potion", "potion", "potion", "card_reward", "card_reward", "card_reward", "relic", "curse" };
        for (int index = 0; index < items.Length; index++)
        {
            var cell = new CrystalSphereCell(0, 0) { IsHidden = false };
            if (items[index] is {} item) cell.SetItem(item);
            board.cells[0, 0] = cell;
            var actual = host.GetState().CrystalSphere!.RevealedCells.Single(c => c.Col == 0 && c.Row == 0);
            Assert.Equal(new CrystalSphereRevealedCell(0, 0, expected[index]), actual);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(actual, Json));
            Assert.Equal(new[] { "col", "row", "content" }, document.RootElement.EnumerateObject().Select(property => property.Name));
        }
    }

    [Fact]
    public void SamePublicKind_DoesNotExposeRarityOrInstanceIdentity()
    {
        var (host, board) = Enter();
        string Project(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity rarity)
        {
            var cell = new CrystalSphereCell(0, 0) { IsHidden = false };
            cell.SetItem(new RealItems.CrystalSpherePotion(rarity));
            board.cells[0, 0] = cell;
            return JsonSerializer.Serialize(host.GetState().CrystalSphere, Json);
        }
        string common = Project(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Common);
        Assert.Equal(common, Project(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Rare));
        Assert.Equal(common, Project(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Common));
    }

    [Fact]
    public void RealMinigame_PublicViewTracksActualCells()
    {
        var (host, board) = Enter();
        var initial = host.GetState().CrystalSphere!;
        Assert.All(initial.RevealedCells, cell => Assert.Equal("empty", cell.Content));
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ClickCrystalSphereCell && o.CrystalSphereCell == new Coord(5, 5)));
        var after = host.GetState().CrystalSphere!;
        Assert.Equal(initial.RevealedCells.Count + 9, after.RevealedCells.Count);
        Assert.Contains(after.RevealedCells, cell => cell.Content != "empty");
        Assert.All(after.RevealedCells, cell =>
        {
            Assert.False(board.cells[cell.Col, cell.Row].IsHidden);
            Assert.DoesNotContain(new Coord(cell.Col, cell.Row), after.HiddenCells);
        });
        Assert.Equal(after.Width * after.Height, after.HiddenCells.Count + after.RevealedCells.Count);
    }

    private static (GameHost Host, CrystalSphereMinigame Board) Enter()
    {
        var host = TestNav.StartOnMap("TESTSEED");
        host.EnterEventDebug(Act1EventsTests.ResolveEvent("CrystalSphere"));
        int payment = host.GetState().Event!.Options.Single(o => o.TextKey.Contains("PAYMENT_PLAN")).Index;
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ChooseEventOption && o.EventOptionIndex == payment));
        var board = (CrystalSphereMinigame)typeof(GameHost).GetProperty("PendingCrystalSphere", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        return (host, board);
    }
}
