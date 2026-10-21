using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using Lts2.Harness;
using Xunit;

namespace Lts2.Harness.Tests;

/// <summary>
/// Shared navigation helpers for tests. With all epochs unlocked, every run opens on the Neow
/// ancient event, so reaching the map (or the first combat) means resolving that event first.
/// </summary>
internal static class TestNav
{
    public static void WaitForAttackIntent(GameHost host)
    {
        // 初手が攻撃とは限らない。攻撃予告の変換を検証する局面まで、通常のターンを進める。
        for (int turn = 0; turn < 5; turn++)
        {
            if (host.GetState().Combat!.Enemies.Any(e => e.Intents.Any(i => i.Damage is > 0)))
                return;
            host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.EndTurn));
        }
        throw new InvalidOperationException("No attack intent within five turns.");
    }

    /// <summary>
    /// Start a run and advance to the map: enter the first room (the Neow ancient event) and
    /// take its first option, which finishes the event and leaves the player on the map.
    /// </summary>
    public static GameHost StartOnMap(string seed = "TESTSEED")
    {
        GameHost host = GameHost.StartNewRun(seed);
        host.EnterFirstRoom();
        ResolveOpeningAncient(host);
        return host;
    }

    /// <summary>
    /// If the run is sitting on the opening Neow ancient event, take a benign blessing so the run
    /// proceeds to the map. A no-op if not currently in an event.
    ///
    /// Blessings whose relic has an upon-pickup effect (e.g. Kaleidoscope, which spawns two bonus
    /// card rewards) are skipped, so combat/reward tests start from the normal starting deck rather
    /// than one padded by side-effect rewards.
    /// </summary>
    public static void ResolveOpeningAncient(GameHost host)
    {
        if (host.GetState().Phase != GamePhase.Event)
        {
            return;
        }

        EventModel ev = RunManager.Instance.EventSynchronizer.GetLocalEvent();
        int chosenIndex = -1;
        int fallbackIndex = -1;
        for (int i = 0; i < ev.CurrentOptions.Count; i++)
        {
            MegaCrit.Sts2.Core.Events.EventOption opt = ev.CurrentOptions[i];
            if (opt.IsLocked || opt.IsProceed)
            {
                continue;
            }
            if (fallbackIndex < 0)
            {
                fallbackIndex = i;
            }
            if (opt.Relic is { HasUponPickupEffect: true })
            {
                continue;
            }
            chosenIndex = i;
            break;
        }
        if (chosenIndex < 0)
        {
            chosenIndex = fallbackIndex;
        }

        GameOption pick = host.ListOptions().First(o => o.EventOptionIndex == chosenIndex);
        host.Apply(pick);
        // v0.111では祝福の取得後にも選択が発生する。未解決のまま次の部屋へ入ると停止する。
        for (int step = 0; host.GetState().Phase != GamePhase.Map && step < 20; step++)
        {
            GameState state = host.GetState();
            if (state.Phase == GamePhase.Choice)
                host.ApplyCardChoice(Enumerable.Range(0, state.PendingChoice!.MinSelect).ToArray());
            else if (state.Phase == GamePhase.Reward)
                host.Apply(host.ListOptions().Single(o => o.Kind == OptionKind.ProceedFromRewards));
            else if (state.Phase == GamePhase.BundleChoice)
                host.Apply(host.ListOptions().First(o => o.Kind == OptionKind.ChooseBundle));
            else
                throw new InvalidOperationException($"Opening ancient did not resolve: {state.Phase}");
        }
        Assert.Equal(GamePhase.Map, host.GetState().Phase);
    }

    /// <summary>
    /// Force the local player's HP for tests (e.g. buff to a huge pool so a greedy run survives a
    /// full act, or lower current HP so a heal has room to act). Sets max HP first, then current.
    /// </summary>
    public static void SetHp(GameHost host, int maxHp, int currentHp)
    {
        var creature = host.Run.Players[0].Creature;
        creature.SetMaxHpInternal(maxHp);
        creature.SetCurrentHpInternal(currentHp);
    }

    public static void PreserveTraversalHp(GameHost host)
    {
        // 経路・Act遷移のテスト用。イベントによる最大HPの置換後にも、生存用の準備状態を戻す。
        if (!host.Run.IsGameOver && host.Run.Players[0].Creature.MaxHp < 9999)
            SetHp(host, maxHp: 9999, currentHp: 9999);
    }

    /// <summary>
    /// Give the local player gold for tests that need to afford shop purchases. The game stores
    /// gold as a plain settable field, so this is a direct set rather than a granting command.
    /// </summary>
    public static void AddGold(GameHost host, int amount)
    {
        host.Run.Players[0].Gold += amount;
    }

    /// <summary>
    /// Grant the local player a relic by id (e.g. "TheCourier") and drain the action queue so any
    /// on-obtain effects settle. Used to set up relic-dependent behaviour like shop discounts.
    /// </summary>
    public static void GiveRelic(GameHost host, string relicId)
    {
        RelicModel relic = ModelDb.AllRelics
            .First(r => r.Id.Entry == relicId || r.GetType().Name == relicId).ToMutable();
        MegaCrit.Sts2.Core.Commands.RelicCmd.Obtain(relic, host.Run.Players[0]).GetAwaiter().GetResult();
        RunManager.Instance.ActionExecutor.FinishedExecutingActions().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Give the local player a potion by id (class name, e.g. "FirePotion"), placed in the first
    /// free belt slot. Returns the live mutable potion so tests can read its properties.
    /// </summary>
    public static MegaCrit.Sts2.Core.Models.PotionModel GivePotion(GameHost host, string potionId)
    {
        MegaCrit.Sts2.Core.Models.PotionModel potion = ModelDb.AllPotions
            .First(p => p.Id.Entry == potionId || p.GetType().Name == potionId).ToMutable();
        host.Run.Players[0].AddPotionInternal(potion);
        return potion;
    }

    /// <summary>
    /// Start a run, resolve the opening ancient event, and move into the first reachable room,
    /// which on the standard seeds is the first combat.
    /// </summary>
    public static GameHost MoveIntoFirstCombat(string seed = "TESTSEED")
    {
        GameHost host = StartOnMap(seed);
        GameOption move = host.ListOptions().First(o => o.Kind == OptionKind.MoveTo);
        host.Apply(move);
        Assert.True(host.InCombat, "expected to land in combat after the first move");
        return host;
    }
}
