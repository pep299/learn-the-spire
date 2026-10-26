using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lts2.Harness;
using Sts2Recorder;

Console.SetOut(Console.Error);
try { InitializeRuntime(); }
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
return Serve(args);

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void InitializeRuntime()
{
    Godot.GD.Out = Godot.GD.Err = Console.Error;
    GameRuntime.EnsureInitialized();
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static int Serve(string[] args)
{
Console.InputEncoding=Console.OutputEncoding=new UTF8Encoding(false);
Godot.GD.Out=Console.Error;Godot.GD.Err=Console.Error;
string Arg(string name,string fallback=""){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:fallback;}
var logPath=Arg("--log");
if(logPath.Length==0){Console.Error.WriteLine("--log <新しいJSONLファイル> [--self-test-one-turn] [--seed DLLLOG1]");return 2;}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
using var recorder=new DllRecorder(logPath);
try
{
    if(args.Contains("--verify-shop-projection"))
    {
        recorder.Reset(Arg("--seed","DLLSHOP1"));
        var inventory=MegaCrit.Sts2.Core.Entities.Merchant.MerchantInventory.CreateForNormalMerchant(recorder.Host!.Run.Players[0]);
        var offers=inventory.AllEntries.Select((entry,index)=>PublicData.Offer(entry,index,true,false,false)).ToArray();
        var json=JsonSerializer.SerializeToElement(offers,DllRecorder.Json);
        var relics=json.EnumerateArray().Where(o=>o.GetProperty("kind").GetString()=="MerchantRelicEntry").ToArray();
        bool passed=relics.Length==3&&relics.All(o=>o.GetProperty("modelId").ValueKind==JsonValueKind.String)&&
            relics.Any(o=>!o.GetProperty("affordable").GetBoolean())&&
            json.EnumerateArray().Where(o=>o.GetProperty("kind").GetString()=="MerchantPotionEntry").All(o=>!o.GetProperty("canBuy").GetBoolean());
        recorder.Write("fixture_verification",new{passed,normalGameplay=false,expertData=false,offers});
        Console.WriteLine(JsonSerializer.Serialize(new{passed,logPath,offers},DllRecorder.Json));
        if(!passed)return 1;
    }
    else if(args.Contains("--self-test-one-turn"))
    {
        recorder.Reset(Arg("--seed","DLLLOG1"));
        for(int i=0;recorder.Host!.GetState().Phase!=GamePhase.Combat;i++)
        {
            if(i>=60)throw new InvalidOperationException("60操作以内に最初の戦闘へ到達しなかった");
            var options=recorder.Host.ListOptions();
            int choice=options.ToList().FindIndex(o=>o.Kind==OptionKind.ChooseEventOption);
            recorder.Step(choice>=0?choice:0);
        }
        int turn=recorder.Host.Run.Players[0].PlayerCombatState!.TurnNumber;
        var playable=recorder.Host.ListOptions().ToList().FindIndex(o=>o.Kind==OptionKind.PlayCard);
        if(playable>=0)recorder.Step(playable);
        for(int i=0;recorder.Host.GetState().Phase==GamePhase.Choice;i++)
        {if(i>=20)throw new InvalidOperationException("カード選択が20操作を超過");recorder.Step(0);}
        int end=recorder.Host.ListOptions().ToList().FindIndex(o=>o.Kind==OptionKind.EndTurn);
        if(end<0)throw new InvalidOperationException("ターン終了が候補にない");
        recorder.Step(end);
        int after=recorder.Host.Run.Players[0].PlayerCombatState!.TurnNumber;
        if(after!=turn+1)throw new InvalidOperationException("次のプレイヤーターンへ到達しなかった");
        Console.WriteLine(JsonSerializer.Serialize(new{passed=true,logPath,initialTurn=turn,finalTurn=after},DllRecorder.Json));
    }
    else
    {
        string restore=Arg("--restore-save");
        if(restore.Length>0)
        {
            recorder.RestoreFile(restore,Arg("--seed"));
            Console.WriteLine(JsonSerializer.Serialize(recorder.Observe(),DllRecorder.Json));
        }
        string? line;
        while((line=Console.ReadLine())!=null)
        {
            if(string.IsNullOrWhiteSpace(line))continue;
            using var command=JsonDocument.Parse(line);var data=command.RootElement;
            string cmd=data.GetProperty("cmd").GetString()!;
            if(cmd=="close")break;
            try
            {
                if(cmd=="reset")
                {
                    if(data.TryGetProperty("character",out var character)&&!string.Equals(character.GetString(),"IRONCLAD",StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("この接続はIRONCLADのみ検証済み。他キャラを暗黙に置き換えない。");
                    if(data.TryGetProperty("ascension",out var ascension)&&ascension.GetInt32()!=0)
                        throw new NotSupportedException("この接続の検証対象はAscension 0のみ。");
                    recorder.Reset(data.TryGetProperty("seed",out var seed)?seed.GetString()!:"DLLLOG1",
                        data.TryGetProperty("actGeneration",out var mode)?mode.GetString()!:"seeded_all_unlocked");
                }
                else if(cmd=="restore")recorder.RestoreFile(data.GetProperty("savePath").GetString()!,data.GetProperty("seed").GetString()!);
                else if(cmd=="step")
                {
                    bool hasIndex=data.TryGetProperty("index",out var index),hasCards=data.TryGetProperty("cardIndices",out var cardIndices);
                    if(hasIndex==hasCards)throw new ArgumentException("stepはindexまたはcardIndicesのどちらか一方を指定");
                    if(hasCards)recorder.SelectCards(cardIndices.EnumerateArray().Select(x=>x.GetInt32()).ToArray());
                    else recorder.Step(index.GetInt32());
                }
                else if(cmd!="observe")throw new ArgumentException("cmdはreset/restore/step/observe/close");
                Console.WriteLine(JsonSerializer.Serialize(recorder.Observe(),DllRecorder.Json));
            }
            catch(Exception e){recorder.Write("command_error",new{error=e.ToString()});Console.WriteLine(JsonSerializer.Serialize(new{error=e.Message},DllRecorder.Json));}
        }
    }
    return 0;
}
catch(Exception e){recorder.Write("recording_error",new{error=e.ToString()});Console.Error.WriteLine(e);return 1;}

}

public sealed class DllRecorder:IDisposable
{
    public static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private readonly StreamWriter writer;
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private readonly string privateReplayPrefix;
    private readonly string session="dll-"+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
    private readonly CardProjection cards=new();
    private readonly Dictionary<string,object> offerHistory=new(StringComparer.Ordinal);
    private string? root;
    private long eventSequence,actionSequence,decisionId;
    public GameHost? Host{get;private set;}
    private bool PendingEffect=>Host!=null&&(bool)R(Host,"EffectSuspended");
    private string actGeneration="seeded_all_unlocked";
    private static object R(object value,string name)=>PublicData.Required(value,name);
    private static object? P(object? value,string name)=>PublicData.Property(value,name);
    private static object[] Items(object? value)=>PublicData.Items(value);
    public DllRecorder(string path)
    {
        privateReplayPrefix=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,Path.GetFileNameWithoutExtension(path));
        writer=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)){AutoFlush=true};
        var game=typeof(GameHost).GetProperty("Run")!.PropertyType.Assembly;
        string Hash(string p)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        string gameHash=Hash(game.Location);
        if(gameHash!="9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4")
            throw new InvalidDataException("検証対象のv0.111.0 DLLとハッシュが異なる。観測の対応を再検証する必要がある。");
        Write("recorder_initialized",new{recorderRevision="dll-public-state-111-acceptance-3",observationRevision="public-state-v4",backend="learn_dll",
            gameVersion="v0.111.0",guiReferenceVersion="v0.111.0",sameVersionComparison=false,assemblySha256=gameHash,
            harnessSha256=Hash(typeof(GameHost).Assembly.Location),recorderSha256=Hash(typeof(DllRecorder).Assembly.Location),
            actor="dll_driver",expertData=false,initialization="harness TestMode; start contract and restore provenance in run_started; not the owner GUI profile",
            localization="harness keys/template fallback; no game PCK localization tables loaded",
            schemaNote="入力と次の観測を記録。効果待ちはラン全体の状態で、入力ごとの効果完了や親子関係は推定しない。"});
    }
    public void Write(string kind,object? data,long? action=null)
        =>writer.WriteLine(JsonSerializer.Serialize(new{schema="sts2-gui-semantic-v1",kind,utc=DateTimeOffset.UtcNow,elapsedSeconds=clock.Elapsed.TotalSeconds,
            sessionId=session,processRunId=session,rootRunId=root,eventSequence=++eventSequence,actionSequence=action,decisionId,data},Json));
    public void Reset(string seed,string mode="seeded_all_unlocked")
    {
        bool seededActs=mode switch{"seeded_all_unlocked"=>true,"default"=>false,_=>throw new ArgumentException("actGenerationはseeded_all_unlockedまたはdefault")};
        if(Host!=null)Write("continuity_boundary",new{reason="reset",pendingEffect=PendingEffect});
        GameRuntime.EnsureInitialized();
        actGeneration=mode;
        Host=GameHost.StartNewRun(seed,new[]{MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters.First(c=>c.Id.Entry=="IRONCLAD")},0,seededActs);Host.EnterFirstRoom();
        StartSegment(seed,"new_segment",null);
    }
    public void RestoreFile(string path,string seed)
    {
        if(string.IsNullOrEmpty(seed))throw new ArgumentException("保存復元にはseedを明示");
        byte[] saveBytes=File.ReadAllBytes(path);
        string json=Encoding.UTF8.GetString(saveBytes).TrimStart('\uFEFF');
        using(var document=JsonDocument.Parse(json))
        {
            var data=document.RootElement;var players=data.GetProperty("players");
            if(data.GetProperty("ascension").GetInt32()!=0||players.GetArrayLength()!=1||players[0].GetProperty("character_id").GetString()!="CHARACTER.IRONCLAD")
                throw new NotSupportedException("保存復元の対応範囲は単独IRONCLAD・A0");
            if(data.GetProperty("rng").GetProperty("seed").GetString()!=seed)
                throw new ArgumentException("指定seedが保存内のseedと一致しない");
        }
        string hash=Convert.ToHexString(SHA256.HashData(saveBytes)).ToLowerInvariant();
        if(Host!=null)Write("continuity_boundary",new{reason="restore",pendingEffect=PendingEffect});
        Host=GameHost.RestoreFromJson(json,seed);actGeneration="saved_state";
        StartSegment(seed,"restored_segment",hash);
    }
    private void StartSegment(string seed,string continuity,string? sourceSaveSha256)
    {
        root=Guid.NewGuid().ToString("N");offerHistory.Clear();
        Write("ready",new{mode="dll_host",automaticInput=false,coverage="public projection; GUI equivalence unverified"});
        Write("run_started",new{character=PublicData.Id(Host!.Run.Players[0].Character),ascension=Host.Run.AscensionLevel,actGeneration,continuity,sourceSaveSha256});
        // seedは再現メタデータにのみ置き、観測には渡さない。
        File.WriteAllText(privateReplayPrefix+"."+root+".replay-input.private.json",JsonSerializer.Serialize(new{rootRunId=root,seed,actGeneration,sourceSaveSha256,policyInput=false,gameVersion="v0.111.0"},Json));
        EmitObservation();
    }
    private object Card(object card)=>new{state=cards.Read(card),rendered=(object?)null};
    private object Pile(object pc,string name,bool unordered=false)
    {
        var values=Items(R(R(pc,name),"Cards"));
        if(unordered)values=values.OrderBy(cards.SortKey,StringComparer.Ordinal).ToArray();
        return new{count=values.Length,order=unordered?"unordered":"pile_display",cards=values.Select(card=>unordered
            ? (object)new{state=cards.Read(card,false),rendered=(object?)null}
            : Card(card)).ToArray()};
    }
    private object Power(object power)=>new{id=PublicData.Id(power),amount=R(power,"DisplayAmount"),title=PublicData.Title(power),
        rules=PublicData.Localization(R(power,"Description")),dynamicVars=PublicData.DynamicVars(power),
        facing=power.GetType().Name=="SurroundedPower"?R(power,"Facing").ToString():null,
        applierCombatId=power.GetType().Name=="FlankingPower"?P(P(power,"Applier"),"CombatId"):null,
        targetCombatId=power.GetType().Name=="SandpitPower"?P(P(power,"Target"),"CombatId"):null};
    private object[] Powers(object creature)=>Items(R(creature,"Powers")).Where(p=>(bool)R(p,"IsVisible")).Select(Power).ToArray();
    private static bool IsType(object value,string name){for(Type? t=value.GetType();t!=null;t=t.BaseType)if(t.Name==name)return true;return false;}
    private object[] Enemies(object combat)
        =>Items(R(combat,"Enemies")).Select(enemy=>(object)new{id=R(enemy,"CombatId"),modelId=R(enemy,"ModelId").ToString(),name=R(enemy,"Name").ToString(),
            hp=R(enemy,"CurrentHp"),maxHp=R(enemy,"MaxHp"),block=R(enemy,"Block"),powers=Powers(enemy),
            intents=Items(P(P(enemy,"Monster"),"NextMove") is object move?P(move,"Intents"):null).Select(intent=>new{
                type=P(intent,"IntentType")?.ToString(),title=PublicData.Localization(P(intent,"IntentTitle")),
                damage=IsType(intent,"AttackIntent")?PublicData.Call(intent,"GetSingleDamage",R(combat,"Allies"),enemy):null,
                totalDamage=IsType(intent,"AttackIntent")?PublicData.Call(intent,"GetTotalDamage",R(combat,"Allies"),enemy):null,
                icon=(string?)null,text=(string?)null}).ToArray()}).ToArray();
    private object? Combat(object player)
    {
        var combat=P(Host,"Combat");var pc=P(player,"PlayerCombatState");
        if(combat==null||pc==null||Host!.GetState().Phase is not (GamePhase.Combat or GamePhase.Choice))return null;
        var queue=R(pc,"OrbQueue");
        return new{turn=R(pc,"TurnNumber"),phase=R(pc,"Phase").ToString(),maxEnergy=R(pc,"MaxEnergy"),stars=R(pc,"Stars"),
            hand=Pile(pc,"Hand"),draw=Pile(pc,"DrawPile",true),discard=Pile(pc,"DiscardPile"),exhaust=Pile(pc,"ExhaustPile"),play=Pile(pc,"PlayPile"),
            orbCapacity=R(queue,"Capacity"),orbs=Items(R(queue,"Orbs")).Select(o=>new{id=PublicData.Id(o),title=PublicData.Title(o),passive=R(o,"PassiveVal"),evoke=R(o,"EvokeVal"),rules=PublicData.Localization(R(o,"Description"))}).ToArray(),
            pets=Items(R(pc,"Pets")).Select(p=>new{id=R(p,"CombatId"),modelId=R(p,"ModelId").ToString(),name=R(p,"Name").ToString(),hp=R(p,"CurrentHp"),maxHp=R(p,"MaxHp"),block=R(p,"Block"),powers=Powers(p)}).ToArray(),
            enemies=Enemies(combat)};
    }
    private string Phase(GamePhase phase)=>phase switch{GamePhase.CrystalSphere=>"crystal_sphere",GamePhase.Combat=>"combat",GamePhase.Map=>"map",GamePhase.Choice=>"grid_selection",GamePhase.Event=>"event",GamePhase.Reward=>"rewards",GamePhase.Shop=>"shop",GamePhase.RestSite=>"rest",GamePhase.Treasure=>"treasure",GamePhase.GameOver=>"terminal",GamePhase.BundleChoice=>"bundle_selection",_=>"game_wait"};
    private string ActionId(GameOption option,int index)=>option.Kind switch{
        OptionKind.PlayCard=>"play:"+option.HandIndex+(option.TargetCombatId is uint target?":"+target:""),
        OptionKind.EndTurn=>"end_turn",OptionKind.MoveTo=>"map:"+option.Coord!.Value.Row+":"+option.Coord.Value.Col,
        OptionKind.ChooseEventOption=>"event:"+option.EventOptionIndex,
        _=>"dll:"+index};
    private static string ActionKind(OptionKind kind)=>kind switch{
        OptionKind.OpenShop=>"shop_open",OptionKind.CloseShop=>"shop_close",OptionKind.LeaveShop=>"shop_leave",OptionKind.ClickCrystalSphereCell=>"crystal_sphere_click",OptionKind.SetCrystalSphereTool=>"crystal_sphere_tool",OptionKind.PlayCard=>"play",OptionKind.EndTurn=>"end_turn",OptionKind.MoveTo=>"map",OptionKind.SelectCards=>"select_grid",
        OptionKind.ChooseEventOption=>"event",OptionKind.ChooseBundle=>"choose_bundle",OptionKind.TakeReward=>"reward",
        OptionKind.TakeCardRewardAlternative=>"reward_alternative",OptionKind.TakeTreasureRelic=>"claim_relic",OptionKind.SkipTreasure=>"skip_treasure",
        OptionKind.ChooseRestOption=>"rest",OptionKind.BuyShopItem=>"buy",OptionKind.UsePotion=>"potion",OptionKind.DiscardPotion=>"discard_potion",_=>kind.ToString()};
    public object Observe()
    {
        var host=Host??throw new InvalidOperationException("先にresetが必要");var view=host.GetState();
        var run=host.Run;object player=run.Players[0],creature=R(player,"Creature");var pc=P(player,"PlayerCombatState");
        var rawCombat=P(host,"Combat");bool active=rawCombat!=null&&pc!=null&&view.Phase is GamePhase.Combat or GamePhase.Choice;
        var combat=Combat(player);
        var options=host.ListOptions();var pending=P(P(host,"Selector"),"Pending");
        var actions=options.Select((option,index)=>(object)new{id=ActionId(option,index),kind=ActionKind(option.Kind),label=option.Description,
            detail=new{nativeOptionIndex=index,nativeKind=option.Kind.ToString(),handIndex=option.HandIndex,target=option.TargetCombatId,card=option.Card?.CardId,
                cardDetails=P(option,"CardModel") is object card?Card(card):null,
                selectedCards=Items(P(option,"SelectedCardModels")).Select(Card).ToArray(),
                bundleIndex=option.BundleIndex,rewardType=P(option,"Reward")?.GetType().Name,
                relicId=P(P(option,"Reward"),"Relic") is {} rewardRelic?PublicData.Id(rewardRelic):null,
                shopItemId=option.ShopItemId,shopItemType=option.ShopItemType,shopItemCost=option.ShopItemCost,
                crystalSphereCol=option.CrystalSphereCell?.Col,crystalSphereRow=option.CrystalSphereCell?.Row,crystalSphereTool=option.CrystalSphereTool,
                potionSlot=option.PotionSlot,potionId=option.PotionId,eventOptionIndex=option.EventOptionIndex,
                restOptionId=option.RestOptionId,treasureRelicId=option.TreasureRelicId}}).ToList();
        if(pending!=null)actions.Add(new{id="dll:select_cards",kind="select_grid",label="候補番号の組を選択",
            detail=new{nativeOptionIndex=(int?)null,nativeKind="SelectCards",parameter="cardIndices",min=R(pending,"MinSelect"),max=R(pending,"MaxSelect"),candidateCount=Items(R(pending,"Options")).Length}});
        var offers=new List<object>();
        if(view.Phase==GamePhase.Shop && view.Shop!.InventoryOpen)
        {
            var inventory=R(host,"CurrentShopInventory");
            int index=0;foreach(var entry in Items(R(inventory,"AllEntries")))
            {
                var offer=PublicData.Offer(entry,index,true,false,(bool)R(player,"HasOpenPotionSlots"));offers.Add(offer);
                var model=PublicData.EntryModel(entry);
                if(model!=null&&(bool)R(entry,"IsStocked"))offerHistory.TryAdd(run.CurrentActIndex+":"+run.TotalFloor+":"+index+":"+PublicData.Id(model)+":"+R(entry,"Cost"),new{act=run.CurrentActIndex+1,floor=run.TotalFloor,index,kind=entry.GetType().Name,modelId=PublicData.Id(model),cost=R(entry,"Cost")});
                index++;
            }
        }
        var hand=active?Items(R(R(pc!,"Hand"),"Cards")).Select((card,index)=>(object)new{index,id=PublicData.Id(card),title=PublicData.Title(card),
            cost=(bool)R(R(card,"EnergyCost"),"CostsX")?"X":PublicData.Call(R(card,"EnergyCost"),"GetResolved")!.ToString(),targetType=R(card,"TargetType").ToString(),cardDetails=Card(card),
            canPlay=options.Any(o=>o.Kind==OptionKind.PlayCard&&o.HandIndex==index)}).ToArray():Array.Empty<object>();
        var status=new{act=run.CurrentActIndex+1,floor=run.TotalFloor,hp=R(creature,"CurrentHp"),maxHp=R(creature,"MaxHp"),block=R(creature,"Block"),powers=Powers(creature),
            gold=R(player,"Gold"),energy=active?R(pc!,"Energy"):0,maxEnergy=R(player,"MaxEnergy"),character=PublicData.Id(R(player,"Character")),ascension=run.AscensionLevel,
            actFloor=R(run,"ActFloor"),potions=Items(R(player,"Potions")).Select(PublicData.Id).ToArray(),relics=Items(R(player,"Relics")).Select(PublicData.Id).ToArray(),
            potionBelt=PublicData.Slots(player),relicDetails=Items(R(player,"Relics")).Select(PublicData.Relic).ToArray(),
            deckCards=Items(R(R(player,"Deck"),"Cards")).Select((c,index)=>new{index,cardDetails=Card(c)}).ToArray(),
            deck=Items(R(R(player,"Deck"),"Cards")).Select(c=>PublicData.Id(c)+((int)R(c,"CurrentUpgradeLevel")>0?"+"+R(c,"CurrentUpgradeLevel"):"")).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
            map=PublicData.Map(run),combat,
            room=new{type=P(run,"CurrentRoom")?.GetType().Name,id=P(P(run,"CurrentRoom"),"ModelId")?.ToString()}};
        var selection=pending==null?null:new{min=R(pending,"MinSelect"),max=R(pending,"MaxSelect"),isUpgradeSelection=R(pending,"IsUpgradeSelection"),
            cards=Items(R(pending,"Options")).Select((card,index)=>new{index,cardDetails=Card(card)}).ToArray(),selected=(object?)null,selectedCount=(int?)null};
        var bundles=P(host,"PendingBundles") is {} pendingBundles?Items(pendingBundles).Select(bundle=>Items(bundle).Select(Card).ToArray()).ToArray():null;
        var state=new{phase=Phase(view.Phase),status,turn=active?R(pc!,"TurnNumber"):null,hand,enemies=active?Enemies(rawCombat!):Array.Empty<object>(),offers,
            selection,eventId=view.Event?.EventId,options=view.Event?.Options,rewards=view.Rewards,bundles,treasure=view.Treasure,rest=view.RestSite,
            shop=view.Shop is {} shop?new{inventoryOpen=shop.InventoryOpen}:null,
            crystalSphere=view.CrystalSphere is {} crystal?new{width=crystal.Width,height=crystal.Height,divinationsLeft=crystal.DivinationsLeft,tool=crystal.Tool,
                hiddenCells=crystal.HiddenCells.Select(c=>new{col=c.Col,row=c.Row}).ToArray(),revealedCells=crystal.RevealedCells}:null,
            terminal=view.IsGameOver?(view.IsVictory?"victory":"death"):null};
        return new{state,actions,publicHistory=new{coverage="current_recorded_segment_only",shopOffers=offerHistory.Values.ToArray()},
            resolution=new{engineActionsInProgress=(int?)null,evidence="host_apply_returned_or_initial_boundary",pendingChoice=view.Phase==GamePhase.Choice,pendingEffect=PendingEffect},
            capabilities=new{backend="learn_dll",gameVersion="v0.111.0",actGeneration,presentationActions=false,renderedCardText=false,intentIcons=false,
                supportedCharacters=new[]{"IRONCLAD"},supportedAscensions=new[]{0},explicitMultiCardIndices=true,drawPileIdentities=false,
                localization="keys_or_template_fallback",unavailable=new Dictionary<string,string>()}};
    }
    private object EmitObservation(){var observation=Observe();decisionId++;Write("observation",new{observation,coverage="unverified"});return observation;}
    public void Step(int index)
    {
        var host=Host??throw new InvalidOperationException("先にresetが必要");var options=host.ListOptions();
        if(index<0||index>=options.Count)throw new ArgumentOutOfRangeException(nameof(index));
        var option=options[index];string id=ActionId(option,index);
        var before=Observe();var choice=JsonSerializer.SerializeToElement(before,Json).GetProperty("actions").EnumerateArray().Single(a=>a.GetProperty("id").GetString()==id);
        ApplyDecision(()=>host.Apply(option),id,ActionKind(option.Kind),new{nativeOptionIndex=index,nativeKind=option.Kind.ToString()},choice,before);
    }
    public void SelectCards(IReadOnlyList<int> indices)
    {
        var host=Host??throw new InvalidOperationException("先にresetが必要");
        var pending=P(P(host,"Selector"),"Pending")??throw new InvalidOperationException("No card choice is pending to resolve.");
        var candidates=Items(R(pending,"Options"));
        if(indices.Distinct().Count()!=indices.Count)throw new ArgumentException("Duplicate card index in the selection.",nameof(indices));
        if(indices.Any(i=>i<0||i>=candidates.Length))throw new ArgumentOutOfRangeException(nameof(indices));
        if(indices.Count<(int)R(pending,"MinSelect")||indices.Count>(int)R(pending,"MaxSelect"))throw new ArgumentException("Selection count outside choice limits.",nameof(indices));
        var selected=indices.Select(i=>candidates[i]).ToArray();
        var before=Observe();
        var choice=new{id="dll:select_cards",kind="select_grid",label="Select cards",
            detail=new{nativeOptionIndex=(int?)null,nativeKind="SelectCards",cardIndices=indices.ToArray(),selectedCards=selected.Select(card=>Card(card)).ToArray()}};
        ApplyDecision(()=>host.ApplyCardChoice(indices),"dll:select_cards","select_grid",new{nativeOptionIndex=(int?)null,nativeKind="SelectCards",cardIndices=indices.ToArray()},choice,before);
    }
    private void ApplyDecision(Action apply,string id,string kind,object detail,object choice,object before)
    {
        long seq=++actionSequence;
        Write("action_initiated",new{actionId=id,kind,source="dll_host.Apply",
            detail,choice,observation=before},seq);
        try
        {
            apply();Write("action_status",new{status="host_apply_returned"},seq);
            var after=EmitObservation();Write("action_successor",new{actionId=id,status="next_decision_observed",pendingEffect=PendingEffect,observation=after,
                mechanicalCompletion="host.Apply returned; native GUI equivalence not certified"},seq);
        }
        catch(Exception e){Write("action_status",new{status="failed",error=e.ToString()},seq);throw;}
    }
    public void Dispose(){Write("process_exit",new{pendingEffect=PendingEffect});writer.Dispose();}
}
