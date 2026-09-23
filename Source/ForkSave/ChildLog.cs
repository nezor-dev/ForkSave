using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace ForkSave;

// The child's only channel back to the parent: a file of records, each a level char
// (M/W/E, or R for the final result) plus text, separated by U+001E.
// It is the source of truth for the result, because Mono may reap the child
// before our waitpid sees its exit status.
internal static class ChildLog
{
    private const char Separator = '\u001e';

    private static string path;

    public static void Reset()
    {
        path = Path.Combine(GenFilePaths.SaveDataFolderPath, "ForkSave-child.log");
        File.WriteAllText(path, "");
    }

    public static void Append(char level, string text) => File.AppendAllText(path, level + text + Separator);

    // Forwards the child's log into the real game log; returns whether the child reported success.
    public static bool ReplayAndCheckOk()
    {
        bool ok = false;
        foreach (string record in File.ReadAllText(path).Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries))
        {
            string text = "[ForkSave child] " + record.Substring(1);
            switch (record[0])
            {
                case 'R': ok = record.Substring(1) == "ok"; break;
                case 'E': Log.Error(text); break;
                case 'W': Log.Warning(text); break;
                default: Log.Message(text); break;
            }
        }
        return ok;
    }

    // In the child, Verse.Log would take its lock and call into Unity's native logger,
    // whose locks another (now vanished) thread may have held at fork time.
    public static void CaptureVerseLog(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(ChildLog), nameof(LogPrefix));
        foreach (string name in new[] { "Message", "Warning", "Error" })
        {
            harmony.Patch(AccessTools.Method(typeof(Log), name, new[] { typeof(string) }), prefix);
        }
        foreach (string name in new[] { "WarningOnce", "ErrorOnce" })
        {
            harmony.Patch(AccessTools.Method(typeof(Log), name, new[] { typeof(string), typeof(int) }), prefix);
        }
    }

    private static bool LogPrefix(string text, MethodBase __originalMethod)
    {
        if (!BackgroundAutosave.InChild)
        {
            return true;
        }
        char level = __originalMethod.Name.StartsWith("Error") ? 'E' : __originalMethod.Name.StartsWith("Warning") ? 'W' : 'M';
        Append(level, text);
        return false;
    }
}
