using System.Collections.Generic;
using System.Linq;
using Lts2.Harness;
using Xunit;
using Xunit.Abstractions;

namespace Lts2.Harness.Tests;

/// <summary>
/// <see cref="RulesDecisionEngine"/>で最初の戦闘を進める。合法手だけを返すこと、
/// 防御の80%利用時の優先順位と攻撃対象の規則、戦闘の完了を検証する。
/// </summary>
public sealed class CombatStrategyTests
{
    private readonly ITestOutputHelper _out;
    private readonly RulesDecisionEngine _engine = new();

    public CombatStrategyTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void Defend_OutranksAttackWhenAtLeastEightyPercentIsUseful(int incoming, bool shouldDefend)
    {
        GameHost host = TestNav.MoveIntoFirstCombat();
        TestNav.WaitForAttackIntent(host);
        GameState state = host.GetState();
        GameOption defend = host.ListOptions().First(o => o.Card?.CardId == "DEFEND_IRONCLAD");
        GameOption end = host.ListOptions().Single(o => o.Kind == OptionKind.EndTurn);
        GameOption attack = host.ListOptions().First(o => o.Card?.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack);
        Assert.Equal(5, defend.Card!.Block);
        Assert.Equal(0, state.Players[0].Block);
        EnemyView enemy = state.Combat!.Enemies.First(e => e.Intents.Any(i => i.Damage is not null));
        IntentView intent = enemy.Intents.First(i => i.Damage is not null) with { Damage = incoming, Hits = 1 };
        state = state with { Combat = state.Combat with { Enemies = new[] { enemy with { Intents = new[] { intent } } } } };
        Assert.Same(shouldDefend ? defend : attack, _engine.Recommend(state, new[] { defend, attack, end }));
    }

    [Fact]
    public void BlockAndDrawSkill_RemainsEligibleWithoutIncomingDamage()
    {
        GameHost host = TestNav.MoveIntoFirstCombat();
        var player = host.Run.Players[0];
        player.PlayerCombatState!.Hand.AddInternal(
            host.Combat!.CreateCard<MegaCrit.Sts2.Core.Models.Cards.ShrugItOff>(player));
        GameOption shrug = host.ListOptions().Single(o => o.Card?.CardId == "SHRUG_IT_OFF");
        GameOption end = host.ListOptions().Single(o => o.Kind == OptionKind.EndTurn);
        GameState state = host.GetState();
        state = state with
        {
            Combat = state.Combat! with
            {
                Enemies = state.Combat.Enemies.Select(e => e with { Intents = System.Array.Empty<IntentView>() }).ToArray(),
            },
        };

        Assert.Same(shrug, _engine.Recommend(state, new[] { shrug, end }));
    }

    [Fact]
    public void DefaultStrategy_PlaysOutCombat_LegallyAndByTheRules()
    {
        GameHost host = TestNav.MoveIntoFirstCombat("TESTSEED");

        bool sawPlay = false;
        for (int step = 0; step < 80; step++)
        {
            GameState state = host.GetState();
            if (state.IsGameOver || state.Phase is not (GamePhase.Combat or GamePhase.Choice))
            {
                break;
            }

            IReadOnlyList<GameOption> options = host.ListOptions();

            // A mid-effect card choice can surface (e.g. a discover): the policy doesn't model choices,
            // so resolve it minimally to keep the fight moving.
            if (state.Phase == GamePhase.Choice)
            {
                GameOption resolve = options.First(o => o.Kind == OptionKind.SelectCards);
                host.Apply(resolve);
                continue;
            }

            GameOption? pick = _engine.Recommend(state, options);
            if (pick is null)
            {
                // The policy declined (it has energy but only cards it doesn't act on). That must not
                // happen when out of energy — then it is required to end the turn.
                int idle = state.Players[0].CombatState?.Energy ?? 0;
                Assert.True(idle > 0, "Policy returned no move while out of energy — it should end the turn.");
                host.EndTurn(host.Combat!.Players.Single());
                continue;
            }

            Assert.Contains(pick, options); // never invents an illegal move
            AssertObeysRules(state, options, pick);
            if (pick.Kind == OptionKind.PlayCard)
            {
                sawPlay = true;
            }

            _out.WriteLine($"[{step}] {pick.Kind} {pick.Description}");
            host.Apply(pick);
        }

        GameState end = host.GetState();
        _out.WriteLine($"Ended: phase={end.Phase} gameOver={end.IsGameOver} hp={end.Players[0].CurrentHp}");
        Assert.False(end.Phase is GamePhase.Combat or GamePhase.Choice, "The strategy should finish the combat.");
        Assert.True(sawPlay, "The strategy should have played at least one card.");
    }

    private static void AssertObeysRules(GameState state, IReadOnlyList<GameOption> options, GameOption pick)
    {
        CombatView combat = state.Combat!;
        if (pick.Kind == OptionKind.EndTurn)
        {
            Assert.DoesNotContain(options, o => o.Kind == OptionKind.PlayCard
                && o.Card!.Type is MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack
                    or MegaCrit.Sts2.Core.Entities.Cards.CardType.Power
                    or MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill);
            return;
        }

        Assert.Equal(OptionKind.PlayCard, pick.Kind);
        CardView card = pick.Card!;
        if (card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack && pick.TargetCombatId is { } targetId)
        {
            // A targeted attack must aim at a lowest-health hittable enemy (ties allowed), unless it is a
            // lethal hit securing a kill on some other target.
            EnemyView target = combat.Enemies.Single(e => e.CombatId == targetId);
            int minHp = combat.Enemies.Where(e => e.IsHittable && e.CurrentHp > 0).Min(e => e.CurrentHp);
            bool lethal = (card.Damage ?? 0) >= target.CurrentHp + target.Block;
            Assert.True(target.CurrentHp == minHp || lethal,
                $"Attacked hp={target.CurrentHp} but a weaker enemy (hp={minHp}) was available and this wasn't a kill.");
        }
    }
}
