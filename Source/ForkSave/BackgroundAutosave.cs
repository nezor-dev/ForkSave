using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using UnityEngine.Scripting;
using Verse;

namespace ForkSave;

// Replaces Autosaver.DoAutosave: fork(), let the child write the save from its
// copy-on-write snapshot, keep playing in the parent, and reap the child from Root.Update.
[HarmonyPatch(typeof(Autosaver), nameof(Autosaver.DoAutosave))]
internal static class BackgroundAutosave
{
    private static readonly Func<Autosaver, string> NewAutosaveFileName =
        AccessTools.MethodDelegate<Func<Autosaver, string>>(AccessTools.Method(typeof(Autosaver), "NewAutosaveFileName"));

    // Set by GameDataSaveLoader.SaveGame (and RimSaves' replacement of it) only when the save succeeded.
    private static readonly Traverse LastSaveTick = Traverse.Create(typeof(GameDataSaveLoader)).Field("lastSaveTick");

    internal static bool InChild;

    private static bool warmedUp;
    private static int childPid;
    private static string childFileName;
    private static Game childGame;
    private static int childForkTick;
    private static readonly Stopwatch ChildClock = new Stopwatch();

    private static bool Prefix(Autosaver __instance)
    {
        if (!ForkSaveMod.Settings.enabled || !ForkSaveMod.PlatformSupported || Find.GameInfo.permadeathMode)
        {
            return true;
        }
        if (childPid != 0)
        {
            Log.Warning("[ForkSave] Previous background autosave is still running; skipping this one.");
            return false;
        }
        string fileName = NewAutosaveFileName(__instance);
        if (!warmedUp)
        {
            // Gives a baseline time and JIT-compiles the save path, so the child doesn't have to.
            warmedUp = true;
            SaveBlocking(fileName, "first autosave this session");
            WarmUpChildPath();
            return false;
        }
        return !TryFork(fileName);
    }

    // JIT-compiles and binds what only the child would otherwise run for the first time.
    private static void WarmUpChildPath()
    {
        RuntimeHelpers.PrepareMethod(AccessTools.Method(typeof(BackgroundAutosave), nameof(RunChild)).MethodHandle);
        Marshal.Prelink(AccessTools.Method(typeof(Native), nameof(Native.fork)));
        Marshal.Prelink(AccessTools.Method(typeof(Native), nameof(Native._exit)));
        LastSaveTick.SetValue(LastSaveTick.GetValue<int>());
        ChildLog.Reset();
        ChildLog.Append('M', "warm-up");
    }

    // Returns false when nothing was forked, so the vanilla autosave runs instead.
    private static bool TryFork(string fileName)
    {
        var forkClock = Stopwatch.StartNew();
        GarbageCollector.Mode gcMode = GarbageCollector.Mode.Enabled;
        int pid;
        try
        {
            ChildLog.Reset();
            // Disabled before fork() so the child never runs a collection: a stop-the-world
            // would wait for threads that don't exist in the child.
            gcMode = GarbageCollector.GCMode;
            GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
            pid = Native.fork();
        }
        catch (Exception e)
        {
            GarbageCollector.GCMode = gcMode;
            Log.Warning("[ForkSave] Could not fork: " + e);
            return false;
        }
        if (pid == 0)
        {
            RunChild(fileName);
        }
        int forkErrno = Marshal.GetLastWin32Error();
        GarbageCollector.GCMode = gcMode;
        forkClock.Stop();

        if (pid < 0)
        {
            Log.Warning($"[ForkSave] fork() failed (errno {forkErrno}).");
            return false;
        }
        childPid = pid;
        childFileName = fileName;
        childGame = Current.Game;
        childForkTick = Find.TickManager.TicksGame;
        ChildClock.Restart();
        Log.Message($"[ForkSave] Forked child {pid} to autosave '{fileName}'; fork() blocked the game for {forkClock.ElapsedMilliseconds} ms.");
        return true;
    }

    private static void RunChild(string fileName)
    {
        InChild = true;
        bool ok = false;
        try
        {
            ChildLog.Append('M', "Started saving.");
            LastSaveTick.SetValue(int.MinValue);
            GameDataSaveLoader.SaveGame(fileName);
            ok = LastSaveTick.GetValue<int>() != int.MinValue;
            ChildLog.Append('R', ok ? "ok" : "fail");
        }
        catch (Exception e)
        {
            ChildLog.Append('E', e.ToString());
        }
        finally
        {
            Native._exit(ok ? 0 : 1);
        }
    }

    internal static void Poll()
    {
        if (childPid == 0)
        {
            return;
        }
        if (Native.waitpid(childPid, out _, Native.WNOHANG) == 0)
        {
            if (ChildClock.Elapsed.TotalSeconds < ForkSaveMod.Settings.timeoutSeconds)
            {
                return;
            }
            Native.kill(childPid, Native.SIGKILL);
            Native.waitpid(childPid, out _, 0);
            Log.Warning($"[ForkSave] Child {childPid} exceeded the {ForkSaveMod.Settings.timeoutSeconds:F0} s timeout and was killed.");
        }
        // Finished: either reaped just now, or already reaped by Mono (waitpid returns -1/ECHILD).
        childPid = 0;
        Game game = childGame;
        childGame = null;
        string fileName = childFileName;

        if (ChildLog.ReplayAndCheckOk())
        {
            Log.Message($"[ForkSave] Background autosave '{fileName}' finished in {ChildClock.Elapsed.TotalSeconds:F1} s.");
            if (Current.Game == game)
            {
                LastSaveTick.SetValue(childForkTick);
            }
            return;
        }
        Log.Warning($"[ForkSave] Background autosave '{fileName}' failed.");
        if (Current.Game == game)
        {
            LongEventHandler.QueueLongEvent(() => SaveBlocking(fileName, "fallback"), "Autosaving", false, null);
        }
    }

    private static void SaveBlocking(string fileName, string reason)
    {
        var clock = Stopwatch.StartNew();
        GameDataSaveLoader.SaveGame(fileName);
        Log.Message($"[ForkSave] Blocking autosave '{fileName}' ({reason}) took {clock.ElapsedMilliseconds} ms.");
    }
}

[HarmonyPatch(typeof(Root), nameof(Root.Update))]
internal static class Root_Update_Patch
{
    private static void Postfix() => BackgroundAutosave.Poll();
}
