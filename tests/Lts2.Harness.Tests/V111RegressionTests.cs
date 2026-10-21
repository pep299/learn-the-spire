using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Relics;

namespace Lts2.Harness.Tests;

public sealed class V111RegressionTests
{
    [Theory]
    [InlineData(0, GamePhase.Reward)]
    [InlineData(1, GamePhase.Map)]
    [InlineData(2, GamePhase.Reward)]
    public async Task EventCombat_ReturnsToParentEffect(int option, GamePhase expected) =>
        await Task.Run(() => EventCombat_ReturnsToParentEffectBody(option, expected)).WaitAsync(TimeSpan.FromSeconds(30));

    private static void EventCombat_ReturnsToParentEffectBody(int option, GamePhase expected)
    {
        GameHost host = EnterDummy(option);
        KillEnemies(host);
        Assert.Equal(expected, host.GetState().Phase);
        if (expected == GamePhase.Reward)
        {
            Assert.Contains(host.ListOptions(), o => o.Kind == OptionKind.TakeReward);
            host.Apply(host.ListOptions().First(o => o.Kind == OptionKind.TakeReward));
            host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ProceedFromRewards));
            Assert.Equal(GamePhase.Map, host.GetState().Phase);
        }
        Assert.Equal(1, host.Run.CurrentRoomCount);
    }

    [Fact]
    public async Task EventCombat_TimeoutResumesParentWithoutRewards() =>
        await Task.Run(() => EventCombat_TimeoutResumesParentWithoutRewardsBody()).WaitAsync(TimeSpan.FromSeconds(30));

    private static void EventCombat_TimeoutResumesParentWithoutRewardsBody()
    {
        GameHost host = EnterDummy(0);
        for (int turn = 0; host.InCombat && turn < 4; turn++)
            host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.EndTurn));
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
        Assert.Equal(1, host.Run.CurrentRoomCount);
        Assert.DoesNotContain(host.ListOptions(), o => o.Kind == OptionKind.TakeReward);
    }

    [Fact]
    public async Task CombatRewardHook_UpdatesCandyOnlyOnce() =>
        await Task.Run(() => CombatRewardHook_UpdatesCandyOnlyOnceBody()).WaitAsync(TimeSpan.FromSeconds(30));

    private static void CombatRewardHook_UpdatesCandyOnlyOnceBody()
    {
        GameHost host = TestNav.StartOnMap();
        var candy = (LastingCandy)host.ObtainRelicDebug(ModelDb.Relic<LastingCandy>());
        host.Apply(host.ListOptions().First(o => o.Kind == OptionKind.MoveTo));
        Assert.True(host.InCombat);
        Assert.Equal(0, candy.CombatRewardsSeen);
        KillEnemies(host);
        Assert.Equal(GamePhase.Reward, host.GetState().Phase);
        Assert.Equal(1, candy.CombatRewardsSeen);
        host.GetState();
        host.ListOptions();
        OfferRewards(host);
        Assert.Equal(1, candy.CombatRewardsSeen);
    }

    [Fact]
    public async Task CompletedRoomTask_PropagatesFaultWithoutChangingState() =>
        await Task.Run(() => CompletedRoomTask_PropagatesFaultWithoutChangingStateBody()).WaitAsync(TimeSpan.FromSeconds(30));

    private static void CompletedRoomTask_PropagatesFaultWithoutChangingStateBody()
    {
        GameHost host = TestNav.StartOnMap();
        string before = JsonSerializer.Serialize(host.GetState());
        var method = typeof(GameHost).GetMethod("PumpRoomTaskUntilIdleOrChoice", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var outer = Assert.Throws<TargetInvocationException>(() => method.Invoke(host,
            new object[] { Task.FromException(new InvalidOperationException("room-fault")), 1000 }));
        Assert.Equal("room-fault", Assert.IsType<InvalidOperationException>(outer.InnerException).Message);
        Assert.Equal(before, JsonSerializer.Serialize(host.GetState()));
    }

    [Fact]
    public async Task RestoreFinishedCombat_OffersRewardsBeforeAnyAutomaticClaim() =>
        await Task.Run(() => RestoreFinishedCombat_OffersRewardsBeforeAnyAutomaticClaimBody()).WaitAsync(TimeSpan.FromSeconds(30));

    private static void RestoreFinishedCombat_OffersRewardsBeforeAnyAutomaticClaimBody()
    {
        GameHost host = TestNav.MoveIntoFirstCombat();
        KillEnemies(host);
        Assert.Equal(GamePhase.Reward, host.GetState().Phase);
        int gold = host.Run.Players[0].Gold;
        string save = host.ToSaveJson();
        GameHost restored = GameHost.RestoreFromJson(save, host.Seed);
        Assert.Equal(gold, restored.Run.Players[0].Gold);
        Assert.Equal(GamePhase.Reward, restored.GetState().Phase);
        Assert.Contains(restored.ListOptions(), o => o.Kind == OptionKind.TakeReward);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreAfterLeavingRewards_DoesNotOfferOrGrantThemAgain(bool claimGold) =>
        await Task.Run(() =>
        {
            GameHost host = TestNav.MoveIntoFirstCombat();
            KillEnemies(host);
            if (claimGold)
                host.Apply(host.ListOptions().First(o => o.Kind == OptionKind.TakeReward && o.Description.EndsWith(" gold")));
            host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ProceedFromRewards));
            Assert.Equal(GamePhase.Map, host.GetState().Phase);
            string expected = DeterminismTests.Signature(host.GetState());
            for (int load = 0; load < 2; load++)
            {
                host = GameHost.RestoreFromJson(host.ToSaveJson(), host.Seed);
                Assert.Equal(expected, DeterminismTests.Signature(host.GetState()));
                Assert.DoesNotContain(host.ListOptions(), o => o.Kind == OptionKind.TakeReward);
                Assert.Contains(host.ListOptions(), o => o.Kind == OptionKind.MoveTo);
            }
        }).WaitAsync(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ParentResume_WaitsForEffectAndPropagatesFault(bool fault, bool disableTracking) =>
        await Task.Run(() => VerifyParentResume(fault, disableTracking)).WaitAsync(TimeSpan.FromSeconds(20));

    private static void VerifyParentResume(bool fault, bool disableTracking)
    {
        GameHost host = EnterDummy(0);
        ResumeProbe.Completed = false;
        ResumeProbe.Fault = fault;
        var harmony = new Harmony("tests.v111.parent-resume");
        var hook = typeof(GameHost).GetField("EventResumeTaskHook", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? original = hook.GetValue(null);
        harmony.Patch(AccessTools.Method(typeof(BattlewornDummy), "Resume"),
            postfix: new HarmonyMethod(typeof(ResumeProbe), nameof(ResumeProbe.Postfix)));
        try
        {
            if (disableTracking) hook.SetValue(null, null);
            KillEnemies(host);
            Assert.False(ResumeProbe.Completed);
            host.Apply(host.ListOptions().First(o => o.Kind == OptionKind.TakeReward));
            var proceed = host.ListOptions().Single(o => o.Kind == OptionKind.ProceedFromRewards);
            if (fault)
            {
                Assert.Equal("resume-fault", Assert.Throws<InvalidOperationException>(() => host.Apply(proceed)).Message);
                Assert.False(host.GetState().IsGameOver);
            }
            else
            {
                host.Apply(proceed);
                Assert.Equal(!disableTracking, ResumeProbe.Completed);
                Assert.Equal(GamePhase.Map, host.GetState().Phase);
            }
        }
        finally
        {
            // 対照試験で追跡を外したタスクも終了させ、次のランへ持ち越さない。
            try { ResumeProbe.LastTask?.GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) when (fault && error.Message == "resume-fault") { }
            finally
            {
                hook.SetValue(null, original);
                harmony.UnpatchAll(harmony.Id);
            }
        }
    }

    private static GameHost EnterDummy(int option)
    {
        GameHost host = TestNav.StartOnMap();
        host.EnterEventDebug(ModelDb.ActsByIndex[2].SelectMany(a => a.AllEvents).OfType<BattlewornDummy>().First());
        host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ChooseEventOption && o.EventOptionIndex == option));
        Assert.True(host.InCombat);
        return host;
    }

    [Fact]
    public async Task MandatoryRelicRewards_SurfaceTheirCardChoiceAndFinish() =>
        await Task.Run(() =>
        {
            GameHost host = TestNav.StartOnMap("TESTSEED");
            int cursesBefore = host.GetState().Players[0].Deck.Count(c => c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Curse);
            host.ObtainRelicDebug(ModelDb.Relic<NeowsBones>());
            Assert.Equal(GamePhase.Reward, host.GetState().Phase);
            Assert.DoesNotContain(host.ListOptions(), o => o.Kind == OptionKind.ProceedFromRewards);
            var proceed = typeof(GameHost).GetMethod("ProceedFromRewards", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var rejected = Assert.Throws<TargetInvocationException>(() => proceed.Invoke(host, null));
            Assert.IsType<InvalidOperationException>(rejected.InnerException);
            host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.TakeReward && o.Description == "Take relic WINGED_BOOTS"));
            host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.TakeReward && o.Description == "Take relic HEFTY_TABLET"));
            Assert.Equal(GamePhase.Choice, host.GetState().Phase);
            host.ApplyCardChoice(new[] { 0 });
            Assert.Equal(GamePhase.Reward, host.GetState().Phase);
            Assert.Equal(cursesBefore + 1, host.GetState().Players[0].Deck.Count(c => c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Curse));
            host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ProceedFromRewards));
            Assert.Equal(GamePhase.Map, host.GetState().Phase);
            Assert.Contains("HEFTY_TABLET", host.GetState().Players[0].Relics);
            Assert.Contains("WINGED_BOOTS", host.GetState().Players[0].Relics);
            Assert.Equal(cursesBefore + 2, host.GetState().Players[0].Deck.Count(c => c.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Curse));
        }).WaitAsync(TimeSpan.FromSeconds(30));

    private static void KillEnemies(GameHost host)
    {
        CreatureCmd.Kill(host.Combat!.Enemies.Where(e => e.IsAlive).ToArray(), force: true).GetAwaiter().GetResult();
        CombatManager.Instance.CheckWinCondition().GetAwaiter().GetResult();
        OfferRewards(host);
    }

    [Fact]
    public async Task EndTurn_ReturnsAfterRoundAdvances() =>
        await Task.Run(() =>
        {
            GameHost host = TestNav.MoveIntoFirstCombat();
            TestNav.SetHp(host, 9999, 9999);
            for (int turn = 0; turn < 6; turn++)
            {
                int before = host.Combat!.RoundNumber;
                host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.EndTurn));
                Assert.True(host.Combat.RoundNumber > before);
                Assert.Contains(host.ListOptions(), o => o.Kind == OptionKind.EndTurn);
            }
        }).WaitAsync(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task EndTurn_WaitsWhenDispatchIsDelayed() =>
        await Task.Run(() =>
        {
            GameHost host = TestNav.MoveIntoFirstCombat();
            int before = host.Combat!.RoundNumber;
            var harmony = new Harmony("tests.v111.delayed-turn");
            harmony.Patch(AccessTools.Method(typeof(CombatManager), "SetReadyToEndTurn"),
                prefix: new HarmonyMethod(typeof(TurnDispatchProbe), nameof(TurnDispatchProbe.Prefix)));
            TurnDispatchProbe.Enabled = true;
            try
            {
                host.EndTurn(host.Run.Players[0]);
                Assert.True(host.Combat.RoundNumber > before, "Returned before the delayed turn began.");
                Assert.Contains(host.ListOptions(), o => o.Kind == OptionKind.EndTurn);
            }
            finally
            {
                TurnDispatchProbe.Deferred?.GetAwaiter().GetResult();
                TurnDispatchProbe.Enabled = false;
                harmony.UnpatchAll(harmony.Id);
            }
        }).WaitAsync(TimeSpan.FromSeconds(30));

    public static class TurnDispatchProbe
    {
        public static bool Enabled;
        public static Task? Deferred;
        public static bool Prefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            if (!Enabled) return true;
            Enabled = false;
            Deferred = Task.Run(async () =>
            {
                await Task.Delay(80);
                __originalMethod.Invoke(__instance, __args);
            });
            return false;
        }
    }

    private static void OfferRewards(GameHost host) =>
        typeof(GameHost).GetMethod("TryOfferCombatRewards", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null);

    public static class ResumeProbe
    {
        public static bool Completed;
        public static bool Fault;
        public static Task? LastTask;
        public static void Postfix(ref Task __result) => __result = LastTask = Finish(__result);
        private static async Task Finish(Task original)
        {
            await original;
            await Task.Delay(80);
            if (Fault) throw new InvalidOperationException("resume-fault");
            Completed = true;
        }
    }
}
