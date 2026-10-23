using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Map;
using Sts2Recorder;
using Lts2.Harness;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace PublicObservation.Tests;

public sealed class PublicRecorderTests
{
    [Fact]
    public void CharacterStart_PreservesThreeArgumentDelegate()
    {
        GameRuntime.EnsureInitialized();
        Func<string, IReadOnlyList<CharacterModel>, int, GameHost> start = GameHost.StartNewRun;
        var character = ModelDb.AllCharacters.First(c => c.Id.Entry == "IRONCLAD");
        GameHost host = start("COMPAT111", new[] { character }, 3);
        Assert.Equal(3, host.Run.AscensionLevel);
        Assert.Equal(character.Id, host.Run.Players.Single().Character.Id);
    }

    [Theory]
    [InlineData("REVIEW5", "SCROLL_BOXES")]
    [InlineData("NATURAL17", "NEOWS_BONES")]
    public void SuspendedRelicEffect_RemainsPendingAtExit(string seed, string relic)
    {
        using var session = new RecorderSession(seed);
        session.Step(o => o.Description.Contains(relic));
        var events = session.CloseAndReadLog();
        Assert.True(events.Single(e => e.GetProperty("kind").GetString() == "action_successor")
            .GetProperty("data").GetProperty("pendingEffect").GetBoolean());
        Assert.True(events.Last().GetProperty("data").GetProperty("pendingEffect").GetBoolean());
    }

    [Fact]
    public void BundleInput_ClearsPendingEffectAfterCardsAreAdded()
    {
        using var session = new RecorderSession("REVIEW5");
        session.Step(o => o.Description.Contains("SCROLL_BOXES"));
        int deck = session.Host.Run.Players[0].Deck.Cards.Count;
        session.Step(o => o.Kind == OptionKind.ChooseBundle);
        Assert.Equal(deck + 3, session.Host.Run.Players[0].Deck.Cards.Count);
        var events = session.CloseAndReadLog();
        Assert.False(events.Last().GetProperty("data").GetProperty("pendingEffect").GetBoolean());
    }

    [Fact]
    public void NestedRewardInputs_ReportEffectWaitWithoutInventingActionParents()
    {
        using var session = new RecorderSession("NATURAL17");
        session.Step(o => o.Description.Contains("NEOWS_BONES"));
        session.Step(o => o.Description == "Take relic LOST_COFFER");
        session.Step(o => o.Kind == OptionKind.TakeReward && o.Card is not null);
        session.Step(o => o.Kind == OptionKind.ProceedFromRewards);
        Assert.Contains(session.Host.ListOptions(), o => o.Description == "Take relic CURSED_PEARL");
        var events = session.CloseAndReadLog();
        Assert.True(events.Last().GetProperty("data").GetProperty("pendingEffect").GetBoolean());
        var inputs = events.Where(e => e.GetProperty("kind").GetString() == "action_initiated").ToArray();
        Assert.Equal(4, inputs.Length);
        Assert.All(inputs, input => Assert.False(input.GetProperty("data").TryGetProperty("parentActionSequence", out _)));
        Assert.All(events.Where(e => e.GetProperty("kind").GetString() == "action_successor"),
            successor => Assert.Equal("next_decision_observed", successor.GetProperty("data").GetProperty("status").GetString()));
    }

    [Fact]
    public async Task OrdinaryCombatReward_IsNotASuspendedInput()
    {
        using var session = new RecorderSession();
        session.ToCombat();
        await CreatureCmd.Kill(session.Host.Combat!.Enemies.Where(e => e.IsAlive).ToArray(), force: true);
        await CombatManager.Instance.CheckWinCondition();
        typeof(GameHost).GetMethod("TryOfferCombatRewards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session.Host, null);
        session.Step(o => o.Kind == OptionKind.TakeReward);
        Assert.Equal(GamePhase.Reward, session.Host.GetState().Phase);
        var events = session.CloseAndReadLog();
        Assert.False(events.Last().GetProperty("data").GetProperty("pendingEffect").GetBoolean());
    }

    [Fact]
    public void SmithSelection_IdentifiesUpgradeWithoutChangingCandidateState()
    {
        using var session = new RecorderSession("7KQ2M0AB9C");
        session.Step(o => o.Description.Contains("NEW_LEAF"));
        session.Recorder.SelectCards(new[] { 2 });
        var rest = session.Host.Run.Map.GetAllMapPoints().First(p => p.PointType == MapPointType.RestSite);
        session.Host.MoveTo(rest.coord);
        session.Step(o => o.RestOptionId == "SMITH");
        using var json = JsonDocument.Parse(session.Observe());
        var selection = json.RootElement.GetProperty("state").GetProperty("selection");
        Assert.True(selection.GetProperty("isUpgradeSelection").GetBoolean());
        Assert.All(selection.GetProperty("cards").EnumerateArray(), card =>
            Assert.Equal(0, card.GetProperty("cardDetails").GetProperty("state").GetProperty("upgrade").GetInt32()));
        Assert.All(session.Host.Run.Players[0].Deck.Cards, card => Assert.False(card.IsUpgraded));
        session.Recorder.SelectCards(new[] { 0 });
        Assert.Single(session.Host.Run.Players[0].Deck.Cards, card => card.IsUpgraded);
    }

    [Fact]
    public async Task UnorderedDraw_DoesNotRevealIdentityOrder() =>
        await Task.Run(() => UnorderedDraw_DoesNotRevealIdentityOrderBody()).WaitAsync(TimeSpan.FromSeconds(30));

    private static void UnorderedDraw_DoesNotRevealIdentityOrderBody()
    {
        using var session = new RecorderSession();
        session.ToCombat();
        var pile = session.Host.Run.Players[0].PlayerCombatState!.DrawPile;
        var mutable = (IList)typeof(CardPile).GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pile)!;
        var projection = new CardProjection();
        var twins = pile.Cards.Select((card, index) => new { card, index })
            .GroupBy(v => projection.SortKey(v.card)).First(g => g.Count() > 1).Take(2).ToArray();
        string before = session.Observe();
        (mutable[twins[0].index], mutable[twins[1].index]) = (mutable[twins[1].index], mutable[twins[0].index]);
        Assert.Same(twins[1].card, pile.Cards[twins[0].index]);
        Assert.Equal(before, session.Observe());
        using var json = JsonDocument.Parse(before);
        foreach (var card in json.RootElement.GetProperty("state").GetProperty("status")
                     .GetProperty("combat").GetProperty("draw").GetProperty("cards").EnumerateArray())
            Assert.Equal(JsonValueKind.Null, card.GetProperty("state").GetProperty("instance").ValueKind);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("non-prefix")]
    [InlineData("all")]
    public async Task MultiCardChoice_PreservesUnselectedCardsAndDiscardsExactInstances(string choice) =>
        await Task.Run(() => MultiCardChoice_PreservesUnselectedCardsAndDiscardsExactInstancesBody(choice)).WaitAsync(TimeSpan.FromSeconds(30));

    private static void MultiCardChoice_PreservesUnselectedCardsAndDiscardsExactInstancesBody(string choice)
    {
        using var session = new RecorderSession();
        session.ToCombat();
        var player = session.Host.Run.Players[0];
        PotionCmd.TryToProcure(ModelDb.Potion<GamblersBrew>().ToMutable(), player, -1).GetAwaiter().GetResult();
        int index = session.Host.ListOptions().ToList().FindIndex(o => o.Kind == OptionKind.UsePotion && o.PotionId == "GAMBLERS_BREW");
        Assert.True(index >= 0);
        session.Recorder.Step(index);
        var combat = player.PlayerCombatState!;
        var hand = combat.Hand.Cards.ToArray();
        string before = session.Observe();
        foreach (var invalid in new[] { new[] { 0, 0 }, new[] { -1 }, new[] { hand.Length } })
        {
            Assert.ThrowsAny<ArgumentException>(() => session.Recorder.SelectCards(invalid));
            Assert.Equal(before, session.Observe());
        }
        int[] selected = choice switch
        {
            "empty" => Array.Empty<int>(),
            "non-prefix" => new[] { 1, 3 },
            _ => Enumerable.Range(0, hand.Length).ToArray()
        };
        int draw = combat.DrawPile.Cards.Count, discarded = combat.DiscardPile.Cards.Count;
        session.Recorder.SelectCards(selected);
        Assert.Equal(GamePhase.Combat, session.Host.GetState().Phase);
        foreach (int i in selected) Assert.Contains(combat.DiscardPile.Cards, c => ReferenceEquals(c, hand[i]));
        foreach (int i in Enumerable.Range(0, hand.Length).Except(selected))
            Assert.Contains(combat.Hand.Cards, c => ReferenceEquals(c, hand[i]));
        Assert.Equal(discarded + selected.Length, combat.DiscardPile.Cards.Count);
        Assert.Equal(draw - selected.Length, combat.DrawPile.Cards.Count);
        Assert.Equal(hand.Length, combat.Hand.Cards.Count);
    }

    [Fact]
    public async Task RequiredChoice_RejectsEmptyAndAcceptsNonFirstCard() =>
        await Task.Run(() => RequiredChoice_RejectsEmptyAndAcceptsNonFirstCardBody()).WaitAsync(TimeSpan.FromSeconds(30));

    private static void RequiredChoice_RejectsEmptyAndAcceptsNonFirstCardBody()
    {
        using var session = new RecorderSession("7KQ2M0AB9C");
        int index = session.Host.ListOptions().ToList().FindIndex(o => o.Description.Contains("NEW_LEAF"));
        Assert.True(index >= 0);
        session.Recorder.Step(index);
        string before = session.Observe();
        Assert.ThrowsAny<ArgumentException>(() => session.Recorder.SelectCards(Array.Empty<int>()));
        Assert.Equal(before, session.Observe());
        session.Recorder.SelectCards(new[] { 2 });
        Assert.Equal(GamePhase.Map, session.Host.GetState().Phase);
    }

    [Fact]
    public async Task Reset_AfterDifferentSeed_ReproducesPublicState() =>
        await Task.Run(() => Reset_AfterDifferentSeed_ReproducesPublicStateBody()).WaitAsync(TimeSpan.FromSeconds(30));

    private static void Reset_AfterDifferentSeed_ReproducesPublicStateBody()
    {
        using var session = new RecorderSession("DLLLOG1");
        string before = WithoutIdentities(session.Observe());
        session.Recorder.Step(0);
        session.Recorder.Reset("native_dev_1", "seeded_all_unlocked");
        Assert.NotEqual(before, WithoutIdentities(session.Observe()));
        session.Recorder.Reset("DLLLOG1", "seeded_all_unlocked");
        Assert.Equal(before, WithoutIdentities(session.Observe()));
    }

    private static string WithoutIdentities(string value)
    {
        var root = JsonNode.Parse(value)!;
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                obj.Remove("instance");
                foreach (var child in obj.ToArray()) Visit(child.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(root);
        return root.ToJsonString();
    }

    private sealed class RecorderSession : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "learn-recorder-test-" + Guid.NewGuid().ToString("N"));
        public DllRecorder Recorder { get; }
        private bool closed;
        public GameHost Host => Recorder.Host!;
        public RecorderSession(string seed = "MULTICARD111")
        {
            Directory.CreateDirectory(directory);
            Recorder = new DllRecorder(Path.Combine(directory, "events.jsonl"));
            Recorder.Reset(seed, "seeded_all_unlocked");
        }
        public string Observe() => JsonSerializer.Serialize(Recorder.Observe());
        public void Step(Func<GameOption, bool> predicate) => Recorder.Step(Host.ListOptions().ToList().FindIndex(o => predicate(o)));
        public JsonElement[] CloseAndReadLog()
        {
            Recorder.Dispose();
            closed = true;
            return File.ReadLines(Path.Combine(directory, "events.jsonl")).Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.Clone();
            }).ToArray();
        }
        public void ToCombat()
        {
            for (int step = 0; Host.GetState().Phase != GamePhase.Combat; step++)
            {
                Assert.True(step < 30, "30操作以内に戦闘へ到達しなかった");
                var options = Host.ListOptions().ToList();
                int pick = options.FindIndex(o => o.Kind == OptionKind.ChooseEventOption);
                Recorder.Step(pick >= 0 ? pick : 0);
            }
        }
        public void Dispose()
        {
            if (!closed) Recorder.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
