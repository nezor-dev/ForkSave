using System.IO;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ForkSave;

public class ForkSaveMod : Mod
{
    public static ForkSaveSettings Settings;

    public static bool ForkAvailable;

    public ForkSaveMod(ModContentPack content) : base(content)
    {
        Settings = GetSettings<ForkSaveSettings>();
        var harmony = new Harmony(content.PackageId);
        // Log capture first: without it the fork patch must not be active.
        ChildLog.CaptureVerseLog(harmony);
        harmony.PatchAll();

        if (Application.platform != RuntimePlatform.LinuxPlayer)
        {
            Log.Warning("[ForkSave] Only works on the native Linux build; autosaves stay vanilla.");
            return;
        }
        string error = Native.LoadForkHelper(Path.Combine(content.RootDir, "1.6", "Assemblies", "libforksave.so"));
        ForkAvailable = error == null;
        if (ForkAvailable)
        {
            Log.Message("[ForkSave] Native fork helper loaded.");
        }
        else
        {
            Log.Warning($"[ForkSave] Native fork helper unavailable ({error}); autosaves stay vanilla.");
        }
    }

    public override string SettingsCategory() => "ForkSave";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        list.CheckboxLabeled("Autosave in a forked background process", ref Settings.enabled);
        list.Label($"Kill a background autosave after {Settings.timeoutSeconds:F0} s and save normally instead");
        Settings.timeoutSeconds = Mathf.Round(list.Slider(Settings.timeoutSeconds, 10f, 600f));
        list.End();
    }
}

public class ForkSaveSettings : ModSettings
{
    public bool enabled = true;
    public float timeoutSeconds = 120f;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref enabled, "enabled", true);
        Scribe_Values.Look(ref timeoutSeconds, "timeoutSeconds", 120f);
    }
}
