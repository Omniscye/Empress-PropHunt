using HarmonyLib;
using UnityEngine;

namespace Empress.REPO.PropHunt
{
    [HarmonyPatch(typeof(WorldSpaceUIPlayerName), "Update")]
    internal static class EmpressPropHuntHidePlayerNamesPatch
    {
        static bool Prefix(WorldSpaceUIPlayerName __instance)
        {
            if (__instance != null && __instance.text != null)
            {
                var c = __instance.text.color;
                __instance.text.color = new Color(c.r, c.g, c.b, 0f);
            }

            return false;
        }
    }
}
