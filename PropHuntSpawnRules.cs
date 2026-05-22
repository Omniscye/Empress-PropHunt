using System.Collections;
using HarmonyLib;

namespace Empress.REPO.PropHunt
{
    [HarmonyPatch(typeof(LevelGenerator), "EnemySetup")]
    internal static class EmpressPropHuntEnemySetupPatch
    {
        static bool Prefix(LevelGenerator __instance, ref IEnumerator __result)
        {
            if (PropHuntPlugin.CfgDisableEnemies == null || !PropHuntPlugin.CfgDisableEnemies.Value) return true;
            __result = SkipEnemySetup(__instance);
            return false;
        }

        static IEnumerator SkipEnemySetup(LevelGenerator levelGenerator)
        {
            GameAccess.SetEnemyGenerationSkipped(levelGenerator);
            yield return null;
        }
    }

    [HarmonyPatch(typeof(ValuableDirector), "Awake")]
    internal static class EmpressPropHuntValuableDirectorPatch
    {
        static void Postfix(ValuableDirector __instance)
        {
            if (PropHuntPlugin.CfgMaxOutValuables == null || !PropHuntPlugin.CfgMaxOutValuables.Value) return;
            GameAccess.SetValuableDebugAll(__instance);
        }
    }
}
