using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NavalNameMod
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        public static ConfigEntry<string> BdfPrefix;
        public static ConfigEntry<string> PalaPrefix;
        public static ConfigEntry<float> SentenceProbability;

        // Roman numeral helpers
        private static readonly string[] Numerals =
        {
            "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X",
            "XI", "XII", "XIII", "XIV", "XV", "XVI", "XVII", "XVIII", "XIX", "XX",
            "XXI", "XXII", "XXIII", "XXIV", "XXV", "XXVI", "XXVII", "XXVIII", "XXIX", "XXX"
        };

        private static string ToRoman(int n) => (n < Numerals.Length) ? Numerals[n] : n.ToString();

        // State - Using STABLE internal keys: "BDF" and "PALA"
        public static readonly Dictionary<string, int> NameUsage = new Dictionary<string, int>();
        
        // Standard names
        public static readonly Dictionary<string, List<string>> NamePool = new Dictionary<string, List<string>>();
        public static readonly Dictionary<string, int> PoolCursor = new Dictionary<string, int>();

        // Sentence names
        public static readonly Dictionary<string, List<string>> SentenceNamePool = new Dictionary<string, List<string>>();
        public static readonly Dictionary<string, int> SentencePoolCursor = new Dictionary<string, int>();

        private void Awake()
        {
            Log = Logger;
            
            BdfPrefix = Config.Bind("General", "BdfPrefix", "BDS", "Prefix used for Boscali (BDF) naval units.");
            PalaPrefix = Config.Bind("General", "PalaPrefix", "PAA", "Prefix used for Primeva (PALA) naval units.");
            SentenceProbability = Config.Bind("General", "SentenceProbability", 0.5f, "Probability (0.0 to 1.0) of a ship getting a sentence name instead of a standard word name. 0.5 is 50%.");

            string pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            
            // Load standard worded names - Mapping files to stable internal keys
            LoadNames(pluginDir, "bdf.txt", "BDF");
            LoadNames(pluginDir, "pala.txt", "PALA");

            // Load sentence names - Mapping file pairs to stable internal keys
            LoadSentenceNames(pluginDir, "bdf-a.txt", "bdf-b.txt", "BDF");
            LoadSentenceNames(pluginDir, "pala-a.txt", "pala-b.txt", "PALA");

            new Harmony(PluginInfo.PLUGIN_GUID).PatchAll();
            Log.LogInfo("[NavalNameMod] Initialized.");
        }

        private static string GetActualFilePath(string dir, string expectedFileName)
        {
            string exactPath = Path.Combine(dir, expectedFileName);
            if (File.Exists(exactPath)) return exactPath;

            try
            {
                var files = Directory.GetFiles(dir, "*.txt");
                foreach (var f in files)
                {
                    if (Path.GetFileName(f).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase)) return f;
                }
            }
            catch { }
            return exactPath;
        }

        private static void LoadNames(string dir, string file, string internalKey)
        {
            string path = GetActualFilePath(dir, file);
            if (!File.Exists(path))
            {
                NamePool[internalKey] = new List<string>();
                Log.LogWarning($"[NavalNameMod] File '{file}' not found for {internalKey}. Standard names disabled.");
                return;
            }
            
            var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            NamePool[internalKey] = lines;
            PoolCursor[internalKey] = 0;
            Log.LogInfo($"[NavalNameMod] Loaded {lines.Count} standard names for {internalKey}.");
        }

        private static void LoadSentenceNames(string dir, string fileA, string fileB, string internalKey)
        {
            string pathA = GetActualFilePath(dir, fileA);
            string pathB = GetActualFilePath(dir, fileB);
            List<string> sentences = new List<string>();

            if (File.Exists(pathA) && File.Exists(pathB))
            {
                var linesA = File.ReadAllLines(pathA).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                var linesB = File.ReadAllLines(pathB).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

                if (linesA.Count > 0 && linesB.Count > 0)
                {
                    foreach (var a in linesA)
                        foreach (var b in linesB)
                            sentences.Add($"{a} {b}");

                    // Shuffle the list for variety
                    var rng = new System.Random();
                    int n = sentences.Count;
                    while (n > 1)
                    {
                        n--;
                        int k = rng.Next(n + 1);
                        string value = sentences[k];
                        sentences[k] = sentences[n];
                        sentences[n] = value;
                    }
                    
                    Log.LogInfo($"[NavalNameMod] Generated and shuffled {sentences.Count} sentence combinations for {internalKey}.");
                }
                else
                {
                    Log.LogWarning($"[NavalNameMod] Sentence files for {internalKey} found but one is empty.");
                }
            }
            else
            {
                Log.LogWarning($"[NavalNameMod] Sentence files for {internalKey} not found ({fileA}/{fileB}).");
            }

            SentenceNamePool[internalKey] = sentences;
            SentencePoolCursor[internalKey] = 0;
        }

        public static string AssignName(string internalKey)
        {
            bool hasStandard = NamePool.ContainsKey(internalKey) && NamePool[internalKey].Count > 0;
            bool hasSentences = SentenceNamePool.ContainsKey(internalKey) && SentenceNamePool[internalKey].Count > 0;

            if (!hasStandard && !hasSentences) return "Unknown";

            bool useSentence = false;
            if (hasStandard && hasSentences)
                useSentence = UnityEngine.Random.value <= Mathf.Clamp01(SentenceProbability.Value);
            else if (hasSentences)
                useSentence = true;

            string baseName;
            if (useSentence)
            {
                var pool = SentenceNamePool[internalKey];
                int cursor = SentencePoolCursor[internalKey];
                baseName = pool[cursor];
                SentencePoolCursor[internalKey] = (cursor + 1) % pool.Count;
            }
            else
            {
                var pool = NamePool[internalKey];
                int cursor = PoolCursor[internalKey];
                baseName = pool[cursor];
                PoolCursor[internalKey] = (cursor + 1) % pool.Count;
            }

            string key = internalKey + "|" + baseName;
            int uses = NameUsage.ContainsKey(key) ? NameUsage[key] : 0;
            NameUsage[key] = uses + 1;

            string prefix = (internalKey == "BDF") ? BdfPrefix.Value : PalaPrefix.Value;
            string finalName = (uses == 0) ? $"{prefix} {baseName}" : $"{prefix} {baseName} {ToRoman(uses + 1)}";

            return finalName;
        }

        public static string GetInternalKey(FactionHQ hq)
        {
            if (hq == null || hq.faction == null) return null;
            if (hq.faction.factionName == FactionHelper.Boscali) return "BDF";
            if (hq.faction.factionName == FactionHelper.Primeva) return "PALA";
            return null;
        }

        // Naming Logic for Navalized Airbases
        public static void TryNameNavalAirbase(Airbase airbase, Unit unit)
        {
            try
            {
                if (airbase == null || unit == null) return;

                // --- Guard: Check Faction ---
                FactionHQ hq = unit.MapHQ;
                if (hq == null) hq = unit.HQ.Value as FactionHQ;

                string internalKey = GetInternalKey(hq);
                if (internalKey == null) return;

                // --- Assign Name ---
                string newName = AssignName(internalKey);
                
                // 1. Update Unit name
                unit.unitName = newName;
                try { unit.NetworkunitName = newName; } catch { }

                // 2. Update Airbase Display Name
                if (airbase.SavedAirbase != null)
                {
                    airbase.SavedAirbase.DisplayName = newName;
                }

                // 3. Update Registry
                try
                {
                    // Note: ChangeAirbaseName might fail if the name is duplicate or registry isn't ready.
                    FactionRegistry.ChangeAirbaseName(airbase, newName);
                }
                catch (Exception e)
                {
                    Log.LogDebug($"[NavalNameMod] FactionRegistry update skipped: {e.Message}");
                }

                Log.LogInfo($"[NavalNameMod] Renamed naval airbase on {unit.gameObject.name} to {newName}.");
            }
            catch (Exception ex)
            {
                Log.LogError($"[NavalNameMod] Error in TryNameNavalAirbase: {ex}");
            }
        }
    }

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

            if (hq != null && hq.faction != null)
            {
                Plugin.TryNameNavalAirbase(airbase, unit);
                Destroy(this);
            }
            else if (framesWaited > 120)
            {
                Destroy(this);
            }
        }
    }

    [HarmonyPatch(typeof(Airbase), "SetupAttachedAirbase")]
    public class Patch_Airbase_SetupAttachedAirbase
    {
        static void Postfix(Airbase __instance, Unit unit)
        {
            if (unit != null && unit.gameObject.GetComponent<AirbaseRenameTask>() == null)
            {
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
        public const string PLUGIN_VERSION = "1.2.0";
    }
}
