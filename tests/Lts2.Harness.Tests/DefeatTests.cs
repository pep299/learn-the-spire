using System;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using Lts2.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Lts2.Harness.Tests;

/// <summary>
/// Defeat and health-reducing events: dying ends the run and leaves no legal action (bar the menu),
/// exactly-lethal damage counts as death. Lethal event choices remain public, carrying the game
/// warning so the policy can distinguish them without changing the available action space.
/// </summary>
public sealed class DefeatTests
{
    private readonly ITestOutputHelper _out;

    public DefeatTests(ITestOutputHelper output) => _out = output;

    private static EventModel ResolveAnyEvent(string typeName) =>
        Enumerable.Range(0, ModelDb.ActsByIndex.Count)
            .SelectMany(i => ModelDb.ActsByIndex[i])
            .SelectMany(a => a.AllEvents)
            .Concat(ModelDb.AllSharedEvents)
            .First(e => e.GetType().Name == typeName);

    private static void KillWithUnblockable(Creature target)
    {
        var ctx = new ThrowingPlayerChoiceContext();
        CreatureCmd.Damage(ctx, target, 99999m, ValueProp.Unblockable | ValueProp.Unpowered, dealer: null, cardSource: null, cardPlay: null)
            .GetAwaiter().GetResult();
        RunManager.Instance.ActionExecutor.FinishedExecutingActions().GetAwaiter().GetResult();
        // A player death marks the combat as a pending loss; the combat flow turns that into an actual
        // end at its next win-condition check (after a card/turn). The direct-damage seam above skips
        // that, so trigger it the way real play would.
        CombatManager.Instance.CheckWinCondition().GetAwaiter().GetResult();
        RunManager.Instance.ActionExecutor.FinishedExecutingActions().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Defeat_InCombat_EndsRun_AndLeavesNoLegalAction()
    {
        await Task.Run(() =>
        {
            GameHost host = TestNav.MoveIntoFirstCombat("TESTSEED");
            KillWithUnblockable(host.Combat!.Players.Single().Creature);

            GameState s = host.GetState();
            _out.WriteLine($"phase={s.Phase} gameOver={s.IsGameOver} victory={s.IsVictory} hp={s.Players[0].CurrentHp} options={host.ListOptions().Count}");
            Assert.True(s.IsGameOver, "the run should be over once the player is dead");
            Assert.False(s.IsVictory);
            Assert.Equal(GamePhase.GameOver, s.Phase);
            Assert.False(host.InCombat);
            Assert.Empty(host.ListOptions()); // nothing to do after death but start a new run
        }).WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task ExactlyLethalDamage_Kills()
    {
        await Task.Run(() =>
        {
            GameHost host = TestNav.MoveIntoFirstCombat("TESTSEED");
            Creature player = host.Combat!.Players.Single().Creature;
            TestNav.SetHp(host, maxHp: player.MaxHp, currentHp: 12);

            var ctx = new ThrowingPlayerChoiceContext();
            CreatureCmd.Damage(ctx, player, 12m, ValueProp.Unblockable | ValueProp.Unpowered, dealer: null, cardSource: null, cardPlay: null)
                .GetAwaiter().GetResult();
            RunManager.Instance.ActionExecutor.FinishedExecutingActions().GetAwaiter().GetResult();

            GameState s = host.GetState();
            _out.WriteLine($"exactly-lethal: hp={s.Players[0].CurrentHp} dead={player.IsDead} gameOver={s.IsGameOver}");
            Assert.Equal(0, s.Players[0].CurrentHp);
            Assert.True(player.IsDead, "0 HP means dead");
            Assert.True(s.IsGameOver);
        }).WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("DollRoom")]
    [InlineData("SlipperyBridge")]
    public async Task Event_ExposesLethalChoice_WithWarningAndRealDeath(string eventName)
    {
        await Task.Run(() => RunEventInvariant(eventName)).WaitAsync(TimeSpan.FromSeconds(120));
    }

    private void RunEventInvariant(string eventName)
    {
        int[] indices = WithFreshEvent(eventName, hp: 3, host =>
            host.ListOptions().Where(o => o.Kind == OptionKind.ChooseEventOption)
                .Select(o => o.EventOptionIndex!.Value).ToArray());

        foreach (int idx in indices)
        {
            WithFreshEvent(eventName, hp: 3, host =>
            {
                GameOption? opt = host.ListOptions()
                    .FirstOrDefault(o => o.Kind == OptionKind.ChooseEventOption && o.EventOptionIndex == idx);
                if (opt is null)
                {
                    return 0;
                }
                bool lethal = host.GetState().Event!.Options.Single(o => o.Index == idx).WillKillPlayer;
                host.Apply(opt);
                GameState s = host.GetState();
                _out.WriteLine($"{eventName} opt {idx}: hp={s.Players[0].CurrentHp} gameOver={s.IsGameOver}");
                Assert.Equal(lethal, s.IsGameOver);
                if (lethal)
                {
                    Assert.Equal(0, s.Players[0].CurrentHp);
                    Assert.False(s.IsVictory);
                    Assert.Empty(host.ListOptions());
                }
                return 0;
            });
        }
    }

    [Fact]
    public async Task Event_HpChangesWarning_WithoutHidingChoices()
    {
        await Task.Run(() =>
        {
            var atFull = WithFreshEvent("DollRoom", hp: 80, host => host.GetState().Event!.Options);
            var atLow = WithFreshEvent("DollRoom", hp: 3, host => host.GetState().Event!.Options);
            Assert.Equal(atFull.Select(o => o.TextKey), atLow.Select(o => o.TextKey));
            Assert.DoesNotContain(atFull, o => o.WillKillPlayer);
            Assert.Contains(atLow, o => o.WillKillPlayer);
        }).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static T WithFreshEvent<T>(string eventName, int hp, Func<GameHost, T> body)
    {
        GameHost host = TestNav.StartOnMap("TESTSEED");
        TestNav.SetHp(host, maxHp: 80, currentHp: hp);
        host.EnterEventDebug(ResolveAnyEvent(eventName));
        return body(host);
    }
}
