using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Optimum.Tests;

// SIGTERM and SIGINT reach WindowExit on the signal thread. Closing the window from there crashed the client
// about 12 s later, before the singleplayer server stopped, so the world was not saved. WindowExit now hands a
// request from another thread to the next frame, and falls back to the old path after 10 s.
public sealed class SignalExitMainThreadTests
{
    [Fact]
    public void WindowExit_DefersOffThreadRequestsBeforeShuttingDown()
    {
        var il = Instructions("WindowExit");

        int threadCheck = il.FindIndex(i => Calls(i, "get_CurrentManagedThreadId"));
        int mainThread = il.FindIndex(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f && f.Name == "MainThreadId");
        int timer = il.FindIndex(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && m.DeclaringType.FullName == "System.Threading.Timer");
        int exiting = il.FindIndex(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).StartsWith("Exiting game now"));
        int shuttingDown = il.FindIndex(i => Calls(i, "set_IsShuttingDown"));

        Assert.True(threadCheck >= 0 && mainThread >= 0, "WindowExit must compare the calling thread with the main thread");
        Assert.True(timer > threadCheck, "an off-thread request must arm the fallback timer");
        Assert.Equal(10000, il.Take(timer).Last(i => i.OpCode == OpCodes.Ldc_I4).Operand);
        Assert.True(exiting > timer && shuttingDown > timer, "the thread check must come before the game starts shutting down");
    }

    [Fact]
    public void RenderFrame_RunsPendingExitBeforeTheFrame()
    {
        var il = Instructions("window_RenderFrame");

        int take = il.FindIndex(i => Calls(i, "TakeOptimumPendingExit"));
        int exit = il.FindIndex(i => Calls(i, "WindowExit"));
        int frame = il.FindIndex(i => Calls(i, "EnsureOptimumDefaults"));

        Assert.True(take >= 0 && exit > take, "the frame must take the pending request and exit with it");
        Assert.True(frame > exit, "the pending exit must run before the frame starts");
    }

    [Fact]
    public void PendingExitMembersShipInTheLib()
    {
        string patcher = System.IO.File.ReadAllText(PatchReader.FindRepositoryFile("Optimum.Patcher/Program.cs"));

        foreach (string member in new[] { "optimumPendingExitLock", "optimumPendingExitReason", "optimumPendingExitMode", "optimumPendingExitTaken", "optimumPendingExitTimer", "TakeOptimumPendingExit", "OptimumPendingExitTimedOut" })
        {
            Assert.Contains($"\"{member}\",", patcher);
        }
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"WindowExit\", 2)", patcher);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"window_RenderFrame\", 1)", patcher);
    }

    private static System.Collections.Generic.List<Instruction> Instructions(string name)
    {
        string path = PatchReader.FindRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        using AssemblyDefinition lib = AssemblyDefinition.ReadAssembly(path);
        return lib.MainModule.GetType("Vintagestory.Client.NoObf.ClientPlatformWindows").Methods.Single(m => m.Name == name).Body.Instructions.ToList();
    }

    private static bool Calls(Instruction i, string name) =>
        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference m && m.Name == name;
}
