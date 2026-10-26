using System.Text.Json;
using Sts2Recorder;
using RealItems = MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;

namespace Lts2.Harness.Tests;

public sealed class CrystalSphereProjectionTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void HiddenCells_DoNotReadItems_AndPrivateChangesDoNotAffectTheOutput()
    {
        var board = new Board(new object[,] { { new Cell(false, null) }, { new Cell(true, new CrystalSphereCurse(), true) } });
        string before = JsonSerializer.Serialize(CrystalSphereProjection.Project(board), Json);
        board.cells[1, 0] = new Cell(true, new CrystalSphereRelic(), true);
        Assert.Equal(before, JsonSerializer.Serialize(CrystalSphereProjection.Project(board), Json));
        Assert.Equal("[{\"col\":0,\"row\":0,\"content\":\"empty\"}]", before);
    }

    [Theory]
    [InlineData("Empty", "empty")]
    [InlineData("Gold", "gold")]
    [InlineData("Potion", "potion")]
    [InlineData("CardReward", "card_reward")]
    [InlineData("Relic", "relic")]
    [InlineData("Curse", "curse")]
    public void VisibleCells_ExposeOnlyCoordinatesAndTheSixContentKinds(string kind, string expected)
    {
        object? item = kind switch
        {
            "Empty" => null, "Gold" => new CrystalSphereGold(), "Potion" => new CrystalSpherePotion(),
            "CardReward" => new CrystalSphereCardReward(), "Relic" => new CrystalSphereRelic(), "Curse" => new CrystalSphereCurse(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var cells = CrystalSphereProjection.Project(new Board(new object[,] { { new Cell(false, item) } }));
        Assert.Equal(new CrystalSphereRevealedCell(0, 0, expected), Assert.Single(cells));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(cells, Json));
        Assert.Equal(new[] { "col", "row", "content" }, document.RootElement[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void SeparateVisibleInstances_DoNotExposeIdentityOrPrivateProperties()
    {
        string Project(object item) => JsonSerializer.Serialize(CrystalSphereProjection.Project(new Board(new object[,] { { new Cell(false, item) } })), Json);
        Assert.Equal(Project(new CrystalSpherePotion()), Project(new CrystalSpherePotion()));
    }

    [Fact]
    public void UnknownVisibleKinds_AreNotReportedAsEmpty()
    {
        Assert.Throws<NotSupportedException>(() => CrystalSphereProjection.Project(new Board(new object[,] { { new Cell(false, new object()) } })));
    }

    [Fact]
    public void MissingItemProperty_IsAnErrorRatherThanAnEmptyCell()
    {
        Assert.Throws<MissingMemberException>(() => CrystalSphereProjection.Project(new Board(new object[,] { { new MissingItemCell() } })));
    }

    [Fact]
    public void RealGameItemVariants_CollapseToTheSixPublicContentKinds()
    {
        var host = TestNav.StartOnMap("TESTSEED");
        var owner = host.Run.Players[0];
        object?[] items =
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
        var observed = items.Select(item => Assert.Single(CrystalSphereProjection.Project(new Board(new object[,] { { new Cell(false, item) } }))).Content).ToArray();
        Assert.Equal(expected, observed);
        Assert.Equal(6, observed.Distinct().Count());
    }

    [Fact]
    public void RealMinigame_PublicViewMatchesTheMaskedContentProjection()
    {
        var host = TestNav.StartOnMap("TESTSEED");
        host.EnterEventDebug(Act1EventsTests.ResolveEvent("CrystalSphere"));
        int payment = host.GetState().Event!.Options.Single(o => o.TextKey.Contains("PAYMENT_PLAN")).Index;
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ChooseEventOption && o.EventOptionIndex == payment));
        var initial = host.GetState().CrystalSphere!;
        Assert.All(initial.RevealedCells, cell => Assert.Equal("empty", cell.Content));
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ClickCrystalSphereCell && o.CrystalSphereCell == new Coord(5, 5)));
        var after = host.GetState().CrystalSphere!;
        Assert.Equal(initial.RevealedCells.Count + 9, after.RevealedCells.Count);
        Assert.Contains(after.RevealedCells, cell => cell.Content != "empty");
        Assert.All(after.RevealedCells, cell => Assert.DoesNotContain(new Coord(cell.Col, cell.Row), after.HiddenCells));
        Assert.Equal(after.Width * after.Height, after.HiddenCells.Count + after.RevealedCells.Count);
        Assert.Equal(CrystalSphereProjection.Project(PublicData.Required(host, "PendingCrystalSphere")), after.RevealedCells);
    }

    private sealed class Board(object[,] cells)
    {
        public readonly object[,] cells = cells;
        public object Items => throw new InvalidOperationException("全配置を読んだ");
    }
    private sealed class Cell(bool hidden, object? item, bool throwOnItem = false)
    {
        public bool IsHidden => hidden;
        public object? Item => throwOnItem ? throw new InvalidOperationException("未開セルの中身を読んだ") : item;
    }
    private sealed class MissingItemCell { public bool IsHidden => false; }
    private abstract class PrivateItem
    {
        public object Position => throw new InvalidOperationException("原点を読んだ");
        public object Size => throw new InvalidOperationException("サイズを読んだ");
        public bool IsGood => throw new InvalidOperationException("正解の良否を読んだ");
        public object Rarity => throw new InvalidOperationException("rarityを読んだ");
    }
    private sealed class CrystalSphereGold : PrivateItem;
    private sealed class CrystalSpherePotion : PrivateItem;
    private sealed class CrystalSphereCardReward : PrivateItem;
    private sealed class CrystalSphereRelic : PrivateItem;
    private sealed class CrystalSphereCurse : PrivateItem;
}
