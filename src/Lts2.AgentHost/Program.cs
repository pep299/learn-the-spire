using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Lts2.Agent;
using Lts2.Agent.Wire;

namespace Lts2.AgentHost;

/// <summary>
/// Headless entry point for the training environment. A remote driver (e.g. a Python trainer) spawns
/// this process and speaks the line protocol over its stdio: one JSON command per line in,
/// one JSON observation per line out. Keeps stdout reserved strictly for protocol messages by routing
/// all game logging to stderr.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Protocol messages are UTF-8 JSON lines; make stdio agree regardless of the console default.
        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = utf8NoBom;
        Console.InputEncoding = utf8NoBom;

        TextWriter protocolOutput = Console.Out;
        Console.SetOut(Console.Error);
        try
        {
            Serve(protocolOutput);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[agent-host] fatal: {ex}");
            return 1;
        }
        finally
        {
            Console.SetOut(protocolOutput);
        }
    }

    // v0.111のSentry初期化はGDを経由せずConsoleへ書く。JIT時の初期化もリダイレクト内に置く。
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Serve(TextWriter protocolOutput)
    {
        Godot.GD.Out = Godot.GD.Err = Console.Error;
        var server = new TrainingEnvironmentServer();
        server.Serve(new StreamLineChannel(Console.In, protocolOutput));
    }
}
