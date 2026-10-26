using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sts2Recorder;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereItems;

namespace Lts2.Harness.Tests;

public sealed class CrystalSphereVisualTests
{
    private const string Root = "res://images/events/crystal_sphere/";
    private static readonly string[] AssetNames = { "big_gold", "gold", "curse", "relic", "common_potion", "uncommon_potion", "rare_potion", "card_outline", "card_back", "card_banner" };
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void RealMinigame_ExposesEveryUncoveredCellThroughThePublicView()
    {
        using var files = Catalog();
        var host = TestNav.StartOnMap("TESTSEED");
        host.ConfigureCrystalAssets(files.Directory);
        host.EnterEventDebug(Act1EventsTests.ResolveEvent("CrystalSphere"));
        int payment = host.GetState().Event!.Options.Single(o => o.TextKey.Contains("PAYMENT_PLAN")).Index;
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ChooseEventOption && o.EventOptionIndex == payment));
        var initial = host.GetState().CrystalSphere!;
        Assert.Null(initial.VisualsUnavailable);
        Assert.Equal(initial.RevealedCells.Count, initial.VisibleFragments.Count);
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ClickCrystalSphereCell && o.CrystalSphereCell == new Coord(5, 5)));
        var after = host.GetState().CrystalSphere!;
        Assert.Null(after.VisualsUnavailable);
        Assert.Equal(initial.RevealedCells.Count + 9, after.VisibleFragments.Count);
        Assert.Contains(after.VisibleFragments, fragment => Decode(fragment.RgbaGzipBase64).Where((_, i) => i % 4 == 3).Any(a => a != 0));
        Assert.Equal(after.RevealedCells.Select(c => (c.Col, c.Row)), after.VisibleFragments.Select(f => (f.Col, f.Row)));
        Assert.All(after.VisibleFragments, fragment => Assert.DoesNotContain(new Coord(fragment.Col, fragment.Row), after.HiddenCells));
        Assert.Empty(after.Items);
    }

    [Theory]
    [InlineData("CrystalSphereCurse", "crystal_sphere_curse")]
    [InlineData("CrystalSphereCardReward", "crystal_sphere_card_reward")]
    [InlineData("HTTPRequest", "http_request")]
    public void CrystalTextureNames_UseTheGodotStringApi(string name, string expected) =>
        Assert.Equal(expected, Godot.StringExtensions.ToSnakeCase(name));

    [Fact]
    public void Templates_AreRunIndependent_AndMatchEveryRealItemRenderingPath()
    {
        using var files = Catalog();
        var templates = CrystalSphereProjection.Templates(files.Assets);
        Assert.Equal(new[] { "gold_small", "gold_big", "curse", "potion_common", "potion_uncommon", "potion_rare", "relic", "card_common", "card_uncommon", "card_rare" }, templates.Select(t => t.Kind));
        Assert.Same(templates, CrystalSphereProjection.Templates(files.Assets));
        var host = TestNav.StartOnMap("TESTSEED");
        var models = new List<(string Kind, CrystalSphereItem Item)>
        {
            ("gold_small", new CrystalSphereGold(false)), ("gold_big", new CrystalSphereGold(true)), ("curse", new CrystalSphereCurse()),
            ("potion_common", new CrystalSpherePotion(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Common)),
            ("potion_uncommon", new CrystalSpherePotion(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Uncommon)),
            ("potion_rare", new CrystalSpherePotion(MegaCrit.Sts2.Core.Entities.Potions.PotionRarity.Rare)), ("relic", new CrystalSphereRelic())
        };
        foreach (var rarity in new[] { MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Common, MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Uncommon, MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Rare })
            models.Add(("card_" + rarity.ToString().ToLowerInvariant(), new CrystalSphereCardReward(rarity, host.Run.Players[0])));
        foreach (var (kind, item) in models)
        {
            var template = templates.Single(t => t.Kind == kind);
            Assert.Equal(item.Size.X, template.WidthCells);
            Assert.Equal(item.Size.Y, template.HeightCells);
            var board = new Board(new Cell[item.Size.X, item.Size.Y]);
            for (int x = 0; x < item.Size.X; x++) for (int y = 0; y < item.Size.Y; y++) board.cells[x, y] = new Cell(false, item);
            var visuals = CrystalSphereProjection.Project(board, files.Assets);
            Assert.Null(visuals.Unavailable);
            byte[] full = Decode(template.RgbaGzipBase64);
            foreach (var actual in visuals.VisibleFragments)
            {
                byte[] crop = Decode(actual.RgbaGzipBase64);
                for (int y = 0; y < 57; y++)
                    Assert.Equal(full.AsSpan(((actual.Row * 57 + y) * template.Width + actual.Col * 57) * 4, 57 * 4).ToArray(), crop.AsSpan(y * 57 * 4, 57 * 4).ToArray());
            }
        }
        Assert.All(templates, template => Assert.Equal(template.Width * template.Height * 4, Decode(template.RgbaGzipBase64).Length));
    }

    [Fact]
    public void HiddenCells_DoNotReadTheirItem_OrAffectPublicFragments()
    {
        using var files = Catalog((Root + "visible.png", Solid(2, 2, 40, 80, 120, 255)));
        var visible = new Cell(false, new Item(Root + "visible.png", new(0, 0), new(2, 2)));
        var board = new Board(new[,] { { visible }, { new Cell(true, null, throwOnItem: true) } });
        string first = JsonSerializer.Serialize(CrystalSphereProjection.Project(board, files.Assets), Json);
        board.cells[1, 0] = new Cell(true, new Item("secret-unavailable-resource", new(1, 0), new(99, 99)), throwOnItem: true);
        Assert.Equal(first, JsonSerializer.Serialize(CrystalSphereProjection.Project(board, files.Assets), Json));
        using var doc = JsonDocument.Parse(first);
        var fragment = doc.RootElement.GetProperty("visibleFragments")[0];
        Assert.Equal(new[] { "col", "row", "width", "height", "rgbaGzipBase64" }, fragment.EnumerateObject().Select(p => p.Name));
        Assert.Single(doc.RootElement.GetProperty("visibleFragments").EnumerateArray());
        Assert.DoesNotContain("secret", first);
        Assert.DoesNotContain("visible.png", first);
    }

    [Fact]
    public void DifferentModelsAndPlacements_WithIdenticalVisiblePixels_HaveIdenticalOutput()
    {
        using var files = Catalog((Root + "one.png", Solid(2, 2, 10, 30, 60, 255)), (Root + "two.png", Solid(3, 3, 10, 30, 60, 255)));
        var board = new Board(new Cell[5, 5]);
        for (int x = 0; x < 5; x++) for (int y = 0; y < 5; y++) board.cells[x, y] = new Cell(true, null, true);
        board.cells[2, 2] = new Cell(false, new Item(Root + "one.png", new(0, 0), new(4, 4)));
        var first = CrystalSphereProjection.Project(board, files.Assets);
        board.cells[2, 2] = new Cell(false, new OtherItem(Root + "two.png", new(1, 1), new(3, 3)));
        Assert.Equal(JsonSerializer.Serialize(first, Json), JsonSerializer.Serialize(CrystalSphereProjection.Project(board, files.Assets), Json));
        using var transparent = Catalog((Root + "two.png", Solid(3, 3, 200, 90, 40, 0)));
        Assert.All(Decode(CrystalSphereProjection.Project(board, transparent.Assets).VisibleFragments[0].RgbaGzipBase64), value => Assert.Equal(0, value));
    }

    [Fact]
    public void CropsFollowIconInsets_AndExcludeUncoveredNeighbors()
    {
        using var files = Catalog((Root + "solid.png", Solid(2, 2, 11, 22, 33, 255)));
        var board = new Board(new[,] { { new Cell(false, new Item(Root + "solid.png", new(0, 0), new(1, 1))) }, { new Cell(true, null, true) } });
        var visuals = CrystalSphereProjection.Project(board, files.Assets);
        var fragment = Assert.Single(visuals.VisibleFragments);
        byte[] pixels = Decode(fragment.RgbaGzipBase64);
        Assert.Equal(57 * 57 * 4, pixels.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels[..4]);
        Assert.Equal(new byte[] { 11, 22, 33, 255 }, pixels.AsSpan((4 * 57 + 6) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels.AsSpan((56 * 57 + 56) * 4, 4).ToArray());
        Assert.Null(visuals.Unavailable);
    }

    [Fact]
    public void MissingCatalogOrAsset_IsUnavailable_InsteadOfAnEmptyValidImage()
    {
        var board = new Board(new[,] { { new Cell(false, new Item(Root + "missing.png", new(0, 0), new(1, 1))) } });
        Assert.NotNull(CrystalSphereProjection.Project(board, null).Unavailable);
        using var files = Catalog();
        var missing = CrystalSphereProjection.Project(board, files.Assets);
        Assert.Empty(missing.VisibleFragments);
        Assert.NotNull(missing.Unavailable);
        Assert.DoesNotContain("missing.png", missing.Unavailable);
    }

    [Fact]
    public void AssetHashes_AreValidatedBeforeObservation()
    {
        using var files = Catalog((Root + "one.png", Solid(2, 2, 1, 2, 3, 255)));
        string path = Path.Combine(files.Directory, "catalog.json");
        string json = File.ReadAllText(path);
        string hash = Convert.ToHexString(SHA256.HashData(Solid(2, 2, 1, 2, 3, 255))).ToLowerInvariant();
        File.WriteAllText(path, json.Replace(hash, new string('0', 64), StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => CrystalSphereAssets.Load(files.Directory));
    }

    private sealed class Board(Cell[,] cells) { public readonly Cell[,] cells = cells; }
    private sealed class Cell(bool hidden, object? item, bool throwOnItem = false)
    {
        public bool IsHidden => hidden;
        public object? Item => throwOnItem ? throw new InvalidOperationException("Hidden item was inspected") : item;
    }
    private readonly record struct Vector(int X, int Y)
    {
        // Godot.Vector2Iと同じ公開フィールドを持たせる。
        public readonly int X = X;
        public readonly int Y = Y;
    }
    private class Item(string texture, Vector position, Vector size)
    {
        public string TexturePath => texture;
        public Vector Position => position;
        public Vector Size => size;
        public bool IsGood => throw new InvalidOperationException("Reward truth was inspected");
    }
    private sealed class OtherItem(string texture, Vector position, Vector size) : Item(texture, position, size);

    private sealed class AssetFiles(string directory, CrystalSphereAssets assets) : IDisposable
    {
        public string Directory => directory;
        public CrystalSphereAssets Assets => assets;
        public void Dispose() => System.IO.Directory.Delete(directory, true);
    }
    private static AssetFiles Catalog(params (string Path, byte[] Rgba)[] images)
    {
        string directory = Path.Combine(Path.GetTempPath(), "lts2-crystal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "catalog.json"), JsonSerializer.Serialize(new
        {
            schema = "crystal-assets-v1", gameVersion = "v0.111.0",
            gameAssemblySha256 = "9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4",
            images = AssetNames.Select(name => (Path: Root + "crystal_sphere_" + name + ".png", Rgba: Solid(2, 2, 1, 2, 3, 255)))
                .Concat(images).GroupBy(image => image.Path).Select(group => group.Last())
                .Select(image => new { path = image.Path, width = image.Rgba.Length == 36 ? 3 : 2, height = image.Rgba.Length == 36 ? 3 : 2,
                rgbaSha256 = Convert.ToHexString(SHA256.HashData(image.Rgba)).ToLowerInvariant(), rgbaGzipBase64 = Encode(image.Rgba) })
        }));
        return new(directory, CrystalSphereAssets.Load(directory));
    }
    private static byte[] Solid(int width, int height, params byte[] color) => Enumerable.Range(0, width * height).SelectMany(_ => color).ToArray();
    private static byte[] Decode(string encoded)
    {
        using var input = new MemoryStream(Convert.FromBase64String(encoded));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); gzip.CopyTo(output); return output.ToArray();
    }
    private static string Encode(byte[] rgba)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true)) gzip.Write(rgba);
        return Convert.ToBase64String(output.ToArray());
    }
}
