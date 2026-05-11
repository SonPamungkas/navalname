using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NavalNameMod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        // -----------------------------------------------------------------------
        // Roman numeral helpers
        // -----------------------------------------------------------------------
        private static readonly string[] Numerals =
        {
            "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X",
            "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX",
            "XXI", "XXII", "XXIII", "XXIV", "XXV", "XXVI", "XXVII", "XXVIII", "XXIX", "XXX"
        };

        private static string ToRoman(int n) => (n < Numerals.Length) ? Numerals[n] : n.ToString();

        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------
        public static readonly Dictionary<string, int> NameUsage = new Dictionary<string, int>();
        public static readonly Dictionary<string, List<string>> NamePool = new Dictionary<string, List<string>>();
        public static readonly Dictionary<string, int> PoolCursor = new Dictionary<string, int>();

        private void Awake()
        {
            Log = Logger;
            string pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            
            LoadNames(pluginDir, "bdf.txt", "BDS");
            LoadNames(pluginDir, "pala.txt", "PAA");

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            Log.LogInfo("[NavalNameMod] Initialized.");
        }

        private static void LoadNames(string dir, string file, string prefix)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                NamePool[prefix] = new List<string>();
                Log.LogWarning($"[NavalNameMod] Could not find {path}");
                return;
            }
            NamePool[prefix] = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            PoolCursor[prefix] = 0;
            Log.LogInfo($"[NavalNameMod] Loaded {NamePool[prefix].Count} names for {prefix}.");
        }

        public static string AssignName(string prefix)
        {
            if (!NamePool.ContainsKey(prefix) || NamePool[prefix].Count == 0) return prefix + " Unknown";

            var pool = NamePool[prefix];
            int cursor = PoolCursor[prefix];
            string baseName = pool[cursor];
            PoolCursor[prefix] = (cursor + 1) % pool.Count;

            string key = prefix + "|" + baseName;
            int uses = NameUsage.ContainsKey(key) ? NameUsage[key] : 0;
            NameUsage[key] = uses + 1;

            return (uses == 0) ? $"{prefix} {baseName}" : $"{prefix} {baseName} {ToRoman(uses + 1)}";
        }

        public static string GetPrefix(FactionHQ hq)
        {
            if (hq == null || hq.faction == null) return null;
            if (hq.faction.factionName == FactionHelper.Boscali) return "BDS";
            if (hq.faction.factionName == FactionHelper.Primeva) return "PAA";
            return null;
        }

        // -----------------------------------------------------------------------
        // Naming Logic for Navalized Airbases
        // -----------------------------------------------------------------------
        public static void TryNameNavalAirbase(Airbase airbase, Unit unit)
        {
            try
            {
                if (airbase == null || unit == null) return;

                // --- Guard: Check Faction ---
                FactionHQ hq = unit.MapHQ;
                if (hq == null) hq = unit.HQ.Value as FactionHQ;

                string prefix = GetPrefix(hq);
                if (prefix == null)
                {
                    Log.LogWarning($"[NavalNameMod] Could not determine prefix for unit {unit.gameObject.name}. Faction might be missing.");
                    return;
                }

                // --- Guard: Check if already named ---
                // NOTE: We have commented out your string.IsNullOrWhiteSpace check!
                // Because the game automatically assigns a default name (like "Compass" or "Revoker"), 
                // unit.unitName is NEVER empty. If we don't comment this out, the mod will ALWAYS skip renaming.
                /*
                if (!string.IsNullOrWhiteSpace(unit.unitName))
                {
                    Log.LogInfo($"[NavalNameMod] Unit {unit.gameObject.name} already has a name: {unit.unitName}. Skipping.");
                    return;
                }
                */

                // --- Assign Name ---
                string newName = AssignName(prefix);
                
                // 1. Update Unit name (for consistency)
                unit.unitName = newName;
                try { unit.NetworkunitName = newName; } catch { }

                // 2. Update Airbase Display Name
                // Note: SavedAirbase might be null for dynamically spawned ships, so we only update it if it exists.
                if (airbase.SavedAirbase != null)
                {
                    airbase.SavedAirbase.DisplayName = newName;
                }

                // 3. Update Registry (Ensures Map/ATC see the new name immediately)
                try
                {
                    FactionRegistry.ChangeAirbaseName(airbase, newName);
                }
                catch (Exception e)
                {
                    Log.LogWarning($"[NavalNameMod] Could not update FactionRegistry: {e.Message}");
                }

                Log.LogInfo($"[NavalNameMod] Renamed naval airbase on {unit.gameObject.name} to {newName}.");
            }
            catch (Exception ex)
            {
                Log.LogError($"[NavalNameMod] Error in TryNameNavalAirbase: {ex}");
            }
        }
    }

    // =========================================================================
    // Delayed Naming Component
    // =========================================================================
    // This script attaches to the unit and waits until the game has fully initialized
    // the Faction/HQ before trying to assign the name.
    public class AirbaseRenameTask : MonoBehaviour
    {
        public Airbase airbase;
        public Unit unit;
        private int framesWaited = 0;

        void Update()
        {
            framesWaited++;
            
            FactionHQ hq = unit.MapHQ;
            if (hq == null) hq = unit.HQ.Value as FactionHQ;

            // Once the game assigns the faction, we can safely rename it
            if (hq != null && hq.faction != null)
            {
                Plugin.TryNameNavalAirbase(airbase, unit);
                Destroy(this); // Job done, remove this script from the unit
            }
            else if (framesWaited > 120) // Give up after roughly 2 seconds
            {
                Plugin.Log.LogWarning($"[NavalNameMod] Gave up waiting for Faction assignment on {unit.gameObject.name}");
                Destroy(this);
            }
        }
    }

    // =========================================================================
    // Patches
    // =========================================================================

    [HarmonyPatch(typeof(Airbase), "SetupAttachedAirbase")]
    public class Patch_Airbase_SetupAttachedAirbase
    {
        static void Postfix(Airbase __instance, Unit unit)
        {
            if (unit != null && unit.gameObject.GetComponent<AirbaseRenameTask>() == null)
            {
                // Attach our waiting task instead of renaming instantly
                var task = unit.gameObject.AddComponent<AirbaseRenameTask>();
                task.airbase = __instance;
                task.unit = unit;
            }
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID    = "com.user.navalnameMod";
        public const string PLUGIN_NAME    = "NavalNameMod";
        public const string PLUGIN_VERSION = "1.1.0";
    }
}