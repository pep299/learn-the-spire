using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lts2.Agent.Wire;
using Lts2.Harness;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

Console.SetOut(Console.Error);
return Execute(args);
[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static int Execute(string[] args)
{
    try
    {
        Godot.GD.Out=Godot.GD.Err=Console.Error;
        GameRuntime.EnsureInitialized();
        var host=GameHost.RestoreFromJson(File.ReadAllText(args[0]),args[1]);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true});
        Report();
        string? input;
        while((input=Console.ReadLine())!=null)
        {
            using var doc=JsonDocument.Parse(input);var command=doc.RootElement;
            if(command.GetProperty("cmd").GetString()=="close")break;
            Apply(command);Report();
        }
        return 0;

        void Apply(JsonElement command)
        {
            if(command.GetProperty("cmd").GetString()=="fixture_kill_all")
            {
                CreatureCmd.Kill(host.Combat!.Enemies.Where(e=>e.IsAlive).ToArray(),force:true).GetAwaiter().GetResult();
                CombatManager.Instance.CheckWinCondition().GetAwaiter().GetResult();
                typeof(GameHost).GetMethod("TryOfferCombatRewards",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(host,null);
            }
            else if(command.TryGetProperty("cardIndices",out var cards))host.ApplyCardChoice(cards.EnumerateArray().Select(v=>v.GetInt32()).ToArray());
            else host.Apply(host.ListOptions()[command.GetProperty("index").GetInt32()]);
        }
        void Report()
        {
            var state=host.GetState();var node=JsonSerializer.SerializeToNode(Observation.From(state,host.ListOptions()),AgentJson.Options)!;
            if(state.Phase is not (GamePhase.Combat or GamePhase.Choice))
                foreach(var player in node["state"]!["players"]!.AsArray())player!["combatState"]=null;
            else
                foreach(var player in node["state"]!["players"]!.AsArray())
                    if(player?["combatState"] is JsonObject combat)
                    {combat["drawPileCount"]=combat["drawPile"]?.AsArray().Count??0;combat.Remove("drawPile");}
            Console.WriteLine(node.ToJsonString());
        }
    }
    catch(Exception error){Console.Error.WriteLine(error);return 1;}
}
