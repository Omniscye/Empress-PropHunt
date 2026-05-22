using HarmonyLib;

namespace Empress.REPO.PropHunt
{
    [HarmonyPatch(typeof(ItemGun), "Shoot")]
    internal static class EmpressPropHuntItemGunShootPatch
    {
        static void Prefix(ItemGun __instance)
        {
            if (IsHunterGun(__instance))
                GameAccess.RefillBattery(__instance);
        }

        static void Postfix(ItemGun __instance)
        {
            if (IsHunterGun(__instance))
                GameAccess.RefillBattery(__instance);
        }

        internal static bool IsHunterGun(ItemGun gun)
        {
            var manager = PropHuntManager.Instance;
            if (manager == null || gun == null) return false;
            var owner = GameAccess.GunOwner(gun);
            return manager.IsActorHunter(manager.ActorNumber(owner));
        }
    }

    [HarmonyPatch(typeof(ItemGun), "ShootRPC")]
    internal static class EmpressPropHuntItemGunShootRpcPatch
    {
        static void Postfix(ItemGun __instance)
        {
            var manager = PropHuntManager.Instance;
            if (manager == null || __instance == null) return;

            if (EmpressPropHuntItemGunShootPatch.IsHunterGun(__instance))
                GameAccess.RefillBattery(__instance);

            if (manager.TryApplyHunterShotCost(__instance))
                manager.TryResolveHunterGunHit(__instance);
        }
    }

    [HarmonyPatch(typeof(ItemBattery), "RemoveFullBar")]
    internal static class EmpressPropHuntBatteryRemoveFullBarPatch
    {
        static bool Prefix(ItemBattery __instance)
        {
            var gun = __instance != null ? __instance.GetComponent<ItemGun>() : null;
            if (!EmpressPropHuntItemGunShootPatch.IsHunterGun(gun)) return true;
            GameAccess.RefillBattery(gun);
            return false;
        }
    }
}
