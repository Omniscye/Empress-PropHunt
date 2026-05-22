
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Empress.REPO.PropHunt;

[BepInPlugin("empress.repo.prophunt.menu", "Empress Prop Hunt Menu", "1.1.2")]
public class MainMenuPrefabSwapPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        gameObject.transform.parent = null;
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        DontDestroyOnLoad(gameObject);
        var h = new Harmony("empress.repo.prophunt.menu");
        h.PatchAll();
        Logger.LogInfo("Empress Prop Hunt Menu loaded");
    }
}

[HarmonyPatch(typeof(LevelGenerator), "StartRoomGeneration")]
public static class Patch_LevelGenerator_StartRoomGeneration
{
    private const string BundleStem = "omnilikeag6";
    private const string PreferredFolder = "Empress-PropHunt";
    private const string PrefabName = "Start Room - Main Menu";
    private const string MusicAssetName = "Constant Music - Main Menu";
    private const string GameMusicClipName = "msc main menu";
    private const string GameMusicSOName = "Constant Music - Main Menu";

    private static AssetBundle _bundle;
    private static GameObject _cachedPrefab;
    private static AudioClip _cachedMusic;
    private static bool _attemptedLoad;

    private static readonly FieldInfo WaitingFlag =
        AccessTools.Field(typeof(LevelGenerator), "waitingForSubCoroutine");

    private static bool InMainMenu()
    {
        var rm = RunManager.instance;
        return rm != null && rm.levelCurrent == rm.levelMainMenu;
    }

    static bool Prefix(LevelGenerator __instance, ref IEnumerator __result)
    {
        if (!InMainMenu())
            return true;

        if (!EnsureAssetsLoaded())
            return true;

        __result = ReplacementCoroutine(__instance, _cachedPrefab, _cachedMusic);
        return false;
    }

    private static IEnumerator ReplacementCoroutine(LevelGenerator gen, GameObject customPrefab, AudioClip music)
    {
        if (WaitingFlag != null) WaitingFlag.SetValue(gen, true);
        gen.State = LevelGenerator.LevelState.StartRoom;
        DisableGameMenuMusic();
        var go = UnityEngine.Object.Instantiate(customPrefab, Vector3.zero, Quaternion.identity);
        foreach (var cam in go.GetComponentsInChildren<Camera>(true))
        {
            if (cam != null && cam.targetTexture != null)
                cam.targetTexture = null;
        }
        if (gen.LevelParent != null)
            go.transform.parent = gen.LevelParent.transform;
        if (music != null)
        {
            var musicGO = new GameObject("MainMenuMusic_Swap");
            musicGO.transform.SetParent(go.transform, false);

            var audio = musicGO.AddComponent<AudioSource>();
            audio.clip = music;
            audio.loop = true;
            audio.playOnAwake = true;
            audio.spatialBlend = 0f;
            audio.volume = 1f;
            audio.priority = 128;

            audio.Play();
        }

        yield return null;

        if (WaitingFlag != null) WaitingFlag.SetValue(gen, false);
    }
    private static void DisableGameMenuMusic()
    {
        int stopped = 0, unloaded = 0, neuteredSO = 0;
        try
        {
            var sources = UnityEngine.Object.FindObjectsOfType<AudioSource>(true);
            foreach (var src in sources)
            {
                var clip = src != null ? src.clip : null;
                if (clip != null && StringEquals(clip.name, GameMusicClipName))
                {
                    try
                    {
                        src.Stop();
                        src.mute = true;
                        src.loop = false;
                        src.clip = null;
                        stopped++;
                    }
                    catch {  }
                }
            }
        }
        catch {  }
        try
        {
            var allClips = Resources.FindObjectsOfTypeAll<AudioClip>();
            foreach (var ac in allClips)
            {
                if (ac != null && StringEquals(ac.name, GameMusicClipName))
                {
                    try
                    {
                        Resources.UnloadAsset(ac);
                        unloaded++;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[Empress Prop Hunt Menu] Couldn't UnloadAsset for '{GameMusicClipName}': {e.Message}");
                    }
                }
            }
        }
        catch {  }
        try
        {
            var allSOs = Resources.FindObjectsOfTypeAll<ScriptableObject>();
            foreach (var so in allSOs)
            {
                if (so == null) continue;
                if (!StringEquals(so.name, GameMusicSOName)) continue;
                var soType = so.GetType();
                bool changed = false;

                var propClip = FindPropertyQuiet(soType, "clip");
                if (propClip != null && typeof(AudioClip).IsAssignableFrom(propClip.PropertyType))
                {
                    try { propClip.SetValue(so, null); changed = true; } catch { }
                }

                var fieldClip = FindFieldQuiet(soType, "clip");
                if (!changed && fieldClip != null && typeof(AudioClip).IsAssignableFrom(fieldClip.FieldType))
                {
                    try { fieldClip.SetValue(so, null); changed = true; } catch { }
                }

                var fieldMusic = FindFieldQuiet(soType, "music");
                if (!changed && fieldMusic != null && typeof(AudioClip).IsAssignableFrom(fieldMusic.FieldType))
                {
                    try { fieldMusic.SetValue(so, null); changed = true; } catch { }
                }

                if (changed)
                {
                    so.hideFlags |= HideFlags.DontSave | HideFlags.HideInHierarchy | HideFlags.HideInInspector;
                    neuteredSO++;
                }
            }
        }
        catch {  }

        Debug.Log($"[Empress Prop Hunt Menu] Stopped {stopped} source(s), unloaded {unloaded} clip(s), updated {neuteredSO} SO(s) for '{GameMusicClipName}'.");
    }

    private static bool EnsureAssetsLoaded()
    {
        if (_attemptedLoad) return _cachedPrefab != null;
        _attemptedLoad = true;

        try
        {
            var bundlePath = FindBundlePath(Paths.PluginPath, BundleStem, PreferredFolder);
            if (bundlePath == null)
            {
                Debug.LogError($"[Empress Prop Hunt Menu] Could not find asset bundle starting with '{BundleStem}' under plugins.");
                return false;
            }

            _bundle = AssetBundle.LoadFromFile(bundlePath);
            if (_bundle == null)
            {
                Debug.LogError($"[Empress Prop Hunt Menu] Failed to load AssetBundle at: {bundlePath}");
                return false;
            }
            _cachedPrefab = _bundle.LoadAsset<GameObject>(PrefabName);
            if (_cachedPrefab == null)
            {
                Debug.LogError($"[Empress Prop Hunt Menu] Prefab '{PrefabName}' not found in bundle: {bundlePath}");
                return false;
            }
            else
            {
                Debug.Log($"[Empress Prop Hunt Menu] Loaded '{PrefabName}' from: {bundlePath}");
            }
            _cachedMusic = TryLoadMusicClip(_bundle, MusicAssetName);
            if (_cachedMusic != null)
            {
                Debug.Log($"[Empress Prop Hunt Menu] Loaded music '{MusicAssetName}'.");
            }
            else
            {
                Debug.LogWarning($"[Empress Prop Hunt Menu] Music asset '{MusicAssetName}' not found or not an AudioClip.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Empress Prop Hunt Menu] Exception loading assets: {ex}");
            return false;
        }

        return true;
    }

    private static AudioClip TryLoadMusicClip(AssetBundle bundle, string name)
    {
        var clip = bundle.LoadAsset<AudioClip>(name);
        if (clip != null) return clip;
        var obj = bundle.LoadAsset(name);
        if (obj == null) return null;

        var type = obj.GetType();
        var prop = FindPropertyQuiet(type, "clip");
        if (prop != null && typeof(AudioClip).IsAssignableFrom(prop.PropertyType))
        {
            try { return prop.GetValue(obj) as AudioClip; } catch { }
        }
        var field = FindFieldQuiet(type, "clip");
        if (field != null && typeof(AudioClip).IsAssignableFrom(field.FieldType))
        {
            try { return field.GetValue(obj) as AudioClip; } catch { }
        }

        return null;
    }
    private static string FindBundlePath(string pluginsRoot, string stem, string preferredFolder)
    {
        if (string.IsNullOrEmpty(pluginsRoot) || !Directory.Exists(pluginsRoot))
        {
            Debug.LogError($"[Empress Prop Hunt Menu] Plugins folder not found: '{pluginsRoot}'");
            return null;
        }

        var candidates = Directory.EnumerateFiles(pluginsRoot, "*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return name != null && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (candidates.Count == 0)
            return null;

        string Pick(string[] arr)
        {
            return arr.OrderByDescending(p => p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                               .Any(seg => seg.Equals(preferredFolder, StringComparison.OrdinalIgnoreCase)))
                      .ThenBy(p => p.Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar))
                      .ThenBy(p => Path.GetFileName(p).Length)
                      .First();
        }

        return Pick(candidates.ToArray());
    }

    private static PropertyInfo FindPropertyQuiet(Type type, string name)
    {
        var currentType = type;
        while (currentType != null)
        {
            var property = AccessTools.GetDeclaredProperties(currentType).FirstOrDefault(p => p.Name == name);
            if (property != null) return property;
            currentType = currentType.BaseType;
        }

        return null;
    }

    private static FieldInfo FindFieldQuiet(Type type, string name)
    {
        var currentType = type;
        while (currentType != null)
        {
            var field = AccessTools.GetDeclaredFields(currentType).FirstOrDefault(f => f.Name == name);
            if (field != null) return field;
            currentType = currentType.BaseType;
        }

        return null;
    }

    private static bool StringEquals(string a, string b)
        => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
