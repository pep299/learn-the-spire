using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lts2.Agent;
using Lts2.Agent.Wire;
using Lts2.Harness;
using MegaCrit.Sts2.Core.Models;

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
Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
Godot.GD.Out = Console.Error;
Godot.GD.Err = Console.Error;
var channel = new PolicyChannel(new StreamLineChannel(Console.In, Console.Out));
try
{
    GameHost? host = args.Length switch
    {
        0 => null,
        2 => GameHost.RestoreFromJson(File.ReadAllText(args[0]), args[1]),
        _ => throw new ArgumentException("expected no arguments, or native save path and seed"),
    };
    void Report() => channel.WriteLine(JsonSerializer.Serialize(
        Observation.From(host!.GetState(), host.ListOptions()), AgentJson.Options));
    if (host is not null) Report();
    string? line;
    while ((line = channel.ReadLine()) != null)
    {
        var command = JsonSerializer.Deserialize<EnvCommand>(line, AgentJson.Options)!;
        if (command.Cmd == "close") break;
        switch (command.Cmd)
        {
            case "reset":
                host = GameHost.StartNewRun(command.Seed!,
                    new[] { ModelDb.AllCharacters.Single(c => c.Id.Entry == "IRONCLAD") },
                    ascension: 0, seededActs: true);
                host.EnterFirstRoom();
                break;
            case "step":
                if (command.CardIndices != null) host!.ApplyCardChoice(command.CardIndices);
                else host!.Apply(host.ListOptions()[command.Index!.Value]);
                break;
            default:
                throw new ArgumentException("comparison host expects reset, step or close");
        }
        Report();
    }
    return channel.Failed ? 1 : 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

}

sealed class PolicyChannel(ILineChannel inner) : ILineChannel
{
    public bool Failed { get; private set; }
    public string? ReadLine() => Failed ? null : inner.ReadLine();

    public void WriteLine(string line)
    {
        var response = JsonNode.Parse(line)!.AsObject();
        Failed = response.ContainsKey("error");
        if (response["state"] is JsonObject state)
        {
            string phase = state["phase"]!.GetValue<string>();
            bool active = phase == "Combat" || phase == "Choice" &&
                state["combat"]?["enemies"]?.AsArray().Any(e =>
                    e?["currentHp"]?.GetValue<int>() > 0) == true;
            foreach (var player in state["players"]!.AsArray())
            {
                if (!active)
                {
                    player!["combatState"] = null;
                }
                else if (player?["combatState"] is JsonObject combat)
                {
                    combat["drawPileCount"] = combat["drawPile"]?.AsArray().Count ?? 0;
                    combat.Remove("drawPile");
                }
            }
            if (!active) state["combat"] = null;
            if (state["crystalSphere"]?["items"] is JsonArray items)
            {
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i]?["revealed"]?.GetValue<bool>() != true)
                        items.RemoveAt(i);
                }
            }
        }
        inner.WriteLine(response.ToJsonString());
    }
}
