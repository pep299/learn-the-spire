using System.Text.Json;
using Lts2.Harness;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using Sts2Recorder;

namespace Lts2.Harness.Tests;

public sealed class PublicCombatContractTests
{
    static PublicCombatContractTests()=>KnownDrawTop.EnsureApplied();
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
    private static PendingChoice Pending(GameHost host)=>(PendingChoice)PublicData.Required(
        PublicData.Required(host,"Selector"),"Pending");

    [Theory]
    [InlineData("Headbutt")]
    [InlineData("ThinkingAhead")]
    public async Task ExplicitPublicTop_KeepsSelectedIdentityAndFactsUntilDraw(string name)
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var combat=host.Combat!;var player=combat.Players.Single();
        var cards=new CardProjection();var projection=new CombatProjection(cards);
        var target=combat.CreateCard<DefendIronclad>(player);
        target.EnergyCost.SetThisCombat(0,false);
        player.PlayerCombatState!.Hand.AddInternal(target);
        CardModel action;
        if(name=="Headbutt")
        {
            await CardPileCmd.Add(target,PileType.Discard,CardPilePosition.Bottom,null,false);
            var other=combat.CreateCard<StrikeIronclad>(player);player.PlayerCombatState.DiscardPile.AddInternal(other);
            action=combat.CreateCard<Headbutt>(player);
        }
        else action=combat.CreateCard<ThinkingAhead>(player);
        player.PlayerCombatState.Hand.AddInternal(action);
        Assert.Empty(projection.DrawKnownTop(player));
        Assert.True(host.PlayCard(action,name=="Headbutt"?combat.Enemies.First():null));
        var pending=Pending(host);
        int index=pending.Options.ToList().IndexOf(target);Assert.True(index>=0);
        host.ApplyCardChoice(new[]{index});
        var known=Assert.Single(Json(projection.DrawKnownTop(player)).EnumerateArray());
        Assert.Equal(cards.Instance(target),known.GetProperty("instance").GetInt64());
        Assert.Equal(0,known.GetProperty("facts").GetProperty("energyCost").GetProperty("local").GetInt32());
        Assert.Equal(0,target.EnergyCost.GetResolved());
        var drawn=await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(),player);
        Assert.Same(target,drawn);
        Assert.Empty(projection.DrawKnownTop(player));
    }

    [Fact]
    public async Task PublicTop_StacksInPublicOrder_PreservesBottom_AndForgetsShuffleOrUnknownInsertion()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var combat=host.Combat!;var player=combat.Players.Single();var cards=new CardProjection();
        var projection=new CombatProjection(cards);var hand=player.PlayerCombatState!.Hand;
        CardModel NewCard(){var card=combat.CreateCard<DefendIronclad>(player);hand.AddInternal(card);return card;}
        long[] Known()=>Json(projection.DrawKnownTop(player)).EnumerateArray().Select(c=>c.GetProperty("instance").GetInt64()).ToArray();
        var first=NewCard();var second=NewCard();var bottom=NewCard();
        await CardPileCmd.Add(first,PileType.Draw,CardPilePosition.Top,null,false);
        await CardPileCmd.Add(second,PileType.Draw,CardPilePosition.Top,null,false);
        await CardPileCmd.Add(bottom,PileType.Draw,CardPilePosition.Bottom,null,false);
        Assert.Equal(new[]{cards.Instance(second),cards.Instance(first)},Known());
        player.PlayerCombatState.DrawPile.MoveToBottomInternal(second);
        Assert.Equal(new[]{cards.Instance(first)},Known());
        var random=NewCard();
        await CardPileCmd.Add(random,PileType.Draw,CardPilePosition.Random,null,false);
        Assert.Empty(Known());
        var next=NewCard();await CardPileCmd.Add(next,PileType.Draw,CardPilePosition.Top,null,false);
        Assert.Single(Known());
        player.PlayerCombatState.DrawPile.RandomizeOrderInternal(player,player.RunState.Rng.Shuffle,combat);
        Assert.Empty(Known());
        var last=NewCard();await CardPileCmd.Add(last,PileType.Draw,CardPilePosition.Top,null,false);
        player.PlayerCombatState.DrawPile.AddInternal(combat.CreateCard<StrikeIronclad>(player),0);
        Assert.Empty(Known());
    }

    [Fact]
    public async Task ReentrantShuffle_CannotRestoreKnowledgeInTheOuterAddPostfix()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");var combat=host.Combat!;
        var player=combat.Players.Single();var projection=new CombatProjection(new CardProjection());
        var pile=player.PlayerCombatState!.DrawPile;
        var card=combat.CreateCard<DefendIronclad>(player);player.PlayerCombatState.Hand.AddInternal(card);
        void Shuffle(CardModel added)=>pile.RandomizeOrderInternal(player,player.RunState.Rng.Shuffle,combat);
        pile.CardAdded+=Shuffle;
        try{await CardPileCmd.Add(card,PileType.Draw,CardPilePosition.Top,null,false);}
        finally{pile.CardAdded-=Shuffle;}
        Assert.Empty(projection.DrawKnownTop(player));
    }

    [Fact]
    public void LocalCosts_PredictBothCleanupOrders_WithoutChangingTheCard()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var player=host.Combat!.Players.Single();
        var card=host.Combat.CreateCard<StrikeIronclad>(player);
        card.EnergyCost.SetThisTurn(0,false);
        card.EnergyCost.AddUntilPlayed(2,false);
        var before=Json(PublicData.EnergyCost(card));
        Assert.Equal(2,before.GetProperty("local").GetInt32());
        Assert.Equal(3,before.GetProperty("afterTurn").GetInt32());
        Assert.Equal(0,before.GetProperty("afterPlay").GetInt32());
        Assert.Equal(1,before.GetProperty("afterTurnAndPlay").GetInt32());
        Assert.Equal(1,before.GetProperty("unmodified").GetInt32());
        var modifiers=before.GetProperty("modifiers").EnumerateArray().ToArray();
        Assert.Equal(new[]{"Absolute","Relative"},modifiers.Select(m=>m.GetProperty("type").GetString()).ToArray());
        Assert.Equal(new[]{2,4},modifiers.Select(m=>m.GetProperty("expiration").GetInt32()).ToArray());
        Assert.All(modifiers,m=>Assert.False(m.GetProperty("isReduceOnly").GetBoolean()));
        Assert.Equal(before.GetRawText(),Json(PublicData.EnergyCost(card)).GetRawText());
        card.EnergyCost.EndOfTurnCleanup();
        Assert.Equal(before.GetProperty("afterTurn").GetInt32(),card.EnergyCost.GetResolved());
        card.EnergyCost.AfterCardPlayedCleanup();
        Assert.Equal(before.GetProperty("afterTurnAndPlay").GetInt32(),card.EnergyCost.GetResolved());

        var other=host.Combat.CreateCard<StrikeIronclad>(player);
        other.EnergyCost.SetThisTurn(0,false);
        other.EnergyCost.AddUntilPlayed(2,false);
        other.EnergyCost.AfterCardPlayedCleanup();
        Assert.Equal(before.GetProperty("afterPlay").GetInt32(),other.EnergyCost.GetResolved());
        other.EnergyCost.EndOfTurnCleanup();
        Assert.Equal(before.GetProperty("afterTurnAndPlay").GetInt32(),other.EnergyCost.GetResolved());

        var either=host.Combat.CreateCard<StrikeIronclad>(player);
        either.EnergyCost.SetThisTurnOrUntilPlayed(0,false);
        var eitherJson=Json(PublicData.EnergyCost(either));
        foreach(string field in new[]{"afterTurn","afterPlay","afterTurnAndPlay"})
            Assert.Equal(1,eitherJson.GetProperty(field).GetInt32());
        var x=Json(PublicData.EnergyCost(host.Combat.CreateCard<Whirlwind>(player)));
        Assert.True(x.GetProperty("costsX").GetBoolean());
        Assert.Equal(-1,x.GetProperty("afterPlay").GetInt32());
    }

    [Fact]
    public void OrderedCostModifiers_PreserveFixedCostThenRelativeAdditionAcrossUpgrade()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var card=host.Combat!.CreateCard<BodySlam>(host.Combat.Players.Single());
        card.EnergyCost.SetThisTurn(0,false);
        card.EnergyCost.AddUntilPlayed(2,false);
        var before=Json(PublicData.EnergyCost(card));
        CardCmd.Upgrade(card,(CardPreviewStyle)0);
        var after=Json(PublicData.EnergyCost(card));
        Assert.Equal(0,after.GetProperty("unmodified").GetInt32());
        Assert.Equal(2,after.GetProperty("local").GetInt32());
        Assert.Equal(2,card.EnergyCost.GetResolved());
        Assert.Equal(before.GetProperty("modifiers").GetRawText(),after.GetProperty("modifiers").GetRawText());
    }

    [Fact]
    public void EnlightenmentThenTransfigure_PreservesReduceOnlyCapAndRelativeAdditionAcrossUpgrade()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var combat=host.Combat!;var player=combat.Players.Single();
        var card=combat.CreateCard<BodySlam>(player);player.PlayerCombatState!.Hand.AddInternal(card);
        var enlightenment=combat.CreateCard<Enlightenment>(player);player.PlayerCombatState.Hand.AddInternal(enlightenment);
        var transfigure=combat.CreateCard<Transfigure>(player);player.PlayerCombatState.Hand.AddInternal(transfigure);
        Assert.True(host.PlayCard(enlightenment,null));
        Assert.True(host.PlayCard(transfigure,null));
        host.ApplyCardChoice(new[]{Pending(host).Options.ToList().IndexOf(card)});
        var before=Json(PublicData.EnergyCost(card));
        var modifiers=before.GetProperty("modifiers").EnumerateArray().ToArray();
        Assert.Equal(2,modifiers.Length);
        Assert.Equal("Absolute",modifiers[0].GetProperty("type").GetString());
        Assert.True(modifiers[0].GetProperty("isReduceOnly").GetBoolean());
        Assert.Equal(1,modifiers[0].GetProperty("amount").GetInt32());
        Assert.Equal(6,modifiers[0].GetProperty("expiration").GetInt32());
        Assert.Equal("Relative",modifiers[1].GetProperty("type").GetString());
        Assert.False(modifiers[1].GetProperty("isReduceOnly").GetBoolean());
        Assert.Equal(1,modifiers[1].GetProperty("amount").GetInt32());
        Assert.Equal(0,modifiers[1].GetProperty("expiration").GetInt32());
        Assert.Equal(2,before.GetProperty("local").GetInt32());
        CardCmd.Upgrade(card,(CardPreviewStyle)0);
        var after=Json(PublicData.EnergyCost(card));
        Assert.Equal(0,after.GetProperty("unmodified").GetInt32());
        Assert.Equal(1,after.GetProperty("local").GetInt32());
        Assert.Equal(1,card.EnergyCost.GetResolved());
        var upgradedModifiers=after.GetProperty("modifiers").EnumerateArray().ToArray();
        Assert.Equal(0,upgradedModifiers[0].GetProperty("amount").GetInt32());
        Assert.Equal(modifiers[1].GetRawText(),upgradedModifiers[1].GetRawText());
    }

    [Fact]
    public async Task PowerAmount_IsIndependentOfDisplayProgress_AndUnownedRelicsHaveNoCombatState()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var creature=host.Combat!.Players.Single().Creature;
        var power=await PowerCmd.Apply<PanachePower>(new ThrowingPlayerChoiceContext(),creature,7,creature,null,false);
        Assert.NotNull(power);
        var value=Json(PublicData.Power(power));
        Assert.Equal(7,value.GetProperty("amount").GetInt32());
        Assert.Equal(5,value.GetProperty("displayAmount").GetInt32());
        foreach(string id in new[]{"PEN_NIB","CENTENNIAL_PUZZLE","VAMBRACE"})
        {
            var relic=ModelDb.AllRelics.Single(r=>r.Id.Entry==id);
            Assert.Equal(JsonValueKind.Null,Json(PublicData.Relic(relic)).GetProperty("effectState").ValueKind);
        }
    }

    [Fact]
    public async Task OwnedRelicState_FollowsActualBlockDamageAndAttackEffects()
    {
        var host=TestNav.StartOnMap("TESTSEED");
        foreach(string name in new[]{"PenNib","CentennialPuzzle","Vambrace"})TestNav.GiveRelic(host,name);
        host.EnterEncounterDebug(Act1FightsTests.ResolveEncounter("SlimesWeak"));
        var combat=host.Combat!;var player=combat.Players.Single();var cards=new CardProjection();
        JsonElement Effect(string id)=>Json(PublicData.Relic(player.Relics.Single(r=>r.Id.Entry==id),cards.Reference)).GetProperty("effectState");
        Assert.False(Effect("VAMBRACE").GetProperty("blockGainedThisCombat").GetBoolean());
        Assert.False(Effect("CENTENNIAL_PUZZLE").GetProperty("usedThisCombat").GetBoolean());
        Assert.Equal(0,Effect("PEN_NIB").GetProperty("attacksPlayed").GetInt32());
        var defend=combat.CreateCard<DefendIronclad>(player);player.PlayerCombatState!.Hand.AddInternal(defend);
        Assert.True(host.PlayCard(defend,null));
        Assert.True(Effect("VAMBRACE").GetProperty("blockGainedThisCombat").GetBoolean());
        Assert.Equal(cards.Instance(defend),Effect("VAMBRACE").GetProperty("triggeringCard").GetProperty("instance").GetInt64());
        var attack=combat.CreateCard<StrikeIronclad>(player);player.PlayerCombatState.Hand.AddInternal(attack);
        Assert.True(host.PlayCard(attack,combat.Enemies.First()));
        Assert.Equal(1,Effect("PEN_NIB").GetProperty("attacksPlayed").GetInt32());
        Assert.Equal(JsonValueKind.Null,Effect("PEN_NIB").GetProperty("attackToDouble").ValueKind);
        await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(),player.Creature,1m,
            ValueProp.Unblockable|ValueProp.Unpowered,null,null,null);
        Assert.True(Effect("CENTENNIAL_PUZZLE").GetProperty("usedThisCombat").GetBoolean());
        var history=Json(new CombatProjection(cards).Read(CombatManager.Instance.History,combat,player));
        Assert.NotEmpty(history.GetProperty("draws").EnumerateArray());
    }

    [Fact]
    public async Task CancelPermission_ComesFromPrefs_WithoutChangingMinimumSelection()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var player=host.Combat!.Players.Single();
        var prefs=new CardSelectorPrefs(new MegaCrit.Sts2.Core.Localization.LocString("card_selection","REMOVE"),2){Cancelable=true};
        var task=CardSelectCmd.FromDeckForRemoval(player,prefs,null);
        var pending=Pending(host);
        Assert.Equal(2,pending.MinSelect);Assert.Equal(2,pending.MaxSelect);
        Assert.NotNull(pending.Context);
        Assert.True(pending.Context.CanCancel);
        Assert.Null(pending.Context.CanSkip);
        Assert.Equal("FromDeckForRemoval",pending.Context.Api);
        var selector=PublicData.Required(host,"Selector");
        PublicData.Call(selector,"Resolve",(object)pending.Options.Take(2).ToArray());
        await task;
    }

    [Fact]
    public async Task BoundConsumption_UsesLivePowerState_AndHistoryStopsAtTheSideTurnBoundary()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var combat=host.Combat!;var player=combat.Players.Single();
        Assert.False(CombatProjection.BoundUsed(player.Creature));
        await PowerCmd.Apply<ChainsOfBindingPower>(new ThrowingPlayerChoiceContext(),player.Creature,1,player.Creature,null,false);
        Assert.False(CombatProjection.BoundUsed(player.Creature));
        var card=combat.CreateCard<DefendIronclad>(player);player.PlayerCombatState!.Hand.AddInternal(card);
        await CardCmd.Afflict<Bound>(card,1);
        Assert.True(host.PlayCard(card,null));
        Assert.True(CombatProjection.BoundUsed(player.Creature));
        var projection=new CombatProjection(new CardProjection());
        Assert.Single(Json(projection.Read(CombatManager.Instance.History,combat,player)).GetProperty("playsStarted").EnumerateArray());
        host.EndTurn(player);
        Assert.False(CombatProjection.BoundUsed(player.Creature));
        Assert.Empty(Json(projection.Read(CombatManager.Instance.History,combat,player)).GetProperty("playsStarted").EnumerateArray());
    }

    [Fact]
    public void History_PairsPlayInstances_AndPreservesBlockSources()
    {
        var host=TestNav.MoveIntoFirstCombat("TESTSEED");
        var combat=host.Combat!;var player=combat.Players.Single();
        var cards=new CardProjection();var projection=new CombatProjection(cards);
        var card=combat.CreateCard<DefendIronclad>(player);
        var hand=player.PlayerCombatState!.Hand;
        hand.AddInternal(card);
        Assert.True(host.PlayCard(card,null));
        player.PlayerCombatState.DiscardPile.RemoveInternal(card);
        hand.AddInternal(card);
        Assert.True(host.PlayCard(card,null));
        var value=Json(projection.Read(CombatManager.Instance.History,combat,player));
        var starts=value.GetProperty("playsStarted").EnumerateArray().Where(e=>e.GetProperty("card").GetProperty("instance").GetInt64()==cards.Instance(card)).ToArray();
        var finishes=value.GetProperty("playsFinished").EnumerateArray().Where(e=>e.GetProperty("card").GetProperty("instance").GetInt64()==cards.Instance(card)).ToArray();
        Assert.Equal(2,starts.Length);Assert.Equal(2,finishes.Length);
        var ids=starts.Select(e=>e.GetProperty("playId").GetInt64()).ToArray();
        Assert.NotEqual(ids[0],ids[1]);
        Assert.Equal(ids,finishes.Select(e=>e.GetProperty("playId").GetInt64()).ToArray());
        var blocks=value.GetProperty("blockGained").EnumerateArray().ToArray();
        Assert.Equal(ids,blocks.Select(e=>e.GetProperty("cardPlay").GetProperty("playId").GetInt64()).ToArray());
        Assert.All(blocks,e=>Assert.Equal(5,e.GetProperty("amount").GetInt32()));
        var sequences=new[]{"playsStarted","playsFinished","blockGained","draws"}
            .SelectMany(name=>value.GetProperty(name).EnumerateArray().Select(e=>e.GetProperty("sequence").GetInt32())).Order().ToArray();
        Assert.Equal(Enumerable.Range(0,sequences.Length),sequences);
    }

    [Theory]
    [InlineData("Toolbox","BeforeHandDraw")]
    [InlineData("ToastyMittens","AfterPlayerTurnStart")]
    [InlineData("GamblingChip","AfterPlayerTurnStart")]
    [InlineData("ChoicesParadox","AfterPlayerTurnStart")]
    public void StartSelection_HasActualRelicSource_AndDoesNotLeakIntoCardPlay(string relic,string hook)
    {
        var host=TestNav.StartOnMap("TESTSEED");
        TestNav.GiveRelic(host,relic);
        host.EnterEncounterDebug(Act1FightsTests.ResolveEncounter("SlimesWeak"));
        var pending=Pending(host);
        Assert.NotNull(pending.Context);
        Assert.Equal("relic",pending.Context.Source!.Kind);
        Assert.Equal(ModelDb.AllRelics.Single(r=>r.GetType().Name==relic).Id.ToString(),pending.Context.Source.ModelId);
        Assert.Equal(hook,pending.Context.Source.Hook);
        Assert.NotNull(pending.Context.Api);
        if(relic=="Toolbox")
        {
            Assert.False(pending.Context.CanSkip);
            Assert.Equal(1,pending.MinSelect);
            Assert.Throws<ArgumentException>(()=>host.ApplyCardChoice(Array.Empty<int>()));
        }
        for(int i=0;host.GetState().Phase==GamePhase.Choice&&i<5;i++)
            host.ApplyCardChoice(Enumerable.Range(0,Pending(host).MinSelect).ToArray());
        Assert.Equal(GamePhase.Combat,host.GetState().Phase);
        var combat=host.Combat!;var player=combat.Players.Single();
        var discovery=combat.CreateCard<Discovery>(player);player.PlayerCombatState!.Hand.AddInternal(discovery);
        Assert.True(host.PlayCard(discovery,null));
        var cardSelection=Pending(host);
        Assert.NotNull(cardSelection.Context);
        Assert.Null(cardSelection.Context.Source);
        Assert.True(cardSelection.Context.CanSkip);
        Assert.Equal(0,cardSelection.MinSelect);
        var projection=new CombatProjection(new CardProjection());
        var during=Json(projection.Read(CombatManager.Instance.History,combat,player));
        var started=Assert.Single(during.GetProperty("playsStarted").EnumerateArray());
        Assert.Empty(during.GetProperty("playsFinished").EnumerateArray());
        host.ApplyCardChoice(new[]{0});
        var after=Json(projection.Read(CombatManager.Instance.History,combat,player));
        Assert.Equal(started.GetProperty("playId").GetInt64(),Assert.Single(after.GetProperty("playsFinished").EnumerateArray()).GetProperty("playId").GetInt64());
    }
}
