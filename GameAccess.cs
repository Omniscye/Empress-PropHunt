using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Empress.REPO.PropHunt
{
    internal static class GameAccess
    {
        static readonly FieldInfo GameDirectorPlayerList = AccessTools.Field(typeof(GameDirector), "PlayerList");
        static readonly FieldInfo GameDirectorCurrentState = AccessTools.Field(typeof(GameDirector), "currentState");
        static readonly FieldInfo PlayerAvatarHealth = AccessTools.Field(typeof(PlayerAvatar), "playerHealth");
        static readonly FieldInfo PlayerHealthValue = AccessTools.Field(typeof(PlayerHealth), "health");
        static readonly FieldInfo PlayerAvatarPhysGrabber = AccessTools.Field(typeof(PlayerAvatar), "physGrabber");
        static readonly FieldInfo PlayerAvatarIsCrouching = AccessTools.Field(typeof(PlayerAvatar), "isCrouching");
        static readonly FieldInfo PhysGrabObjectLastPlayerGrabbing = AccessTools.Field(typeof(PhysGrabObject), "lastPlayerGrabbing");
        static readonly FieldInfo PhysGrabberPlayerAvatar = AccessTools.Field(typeof(PhysGrabber), "playerAvatar");
        static readonly FieldInfo ItemGunPhysGrabObject = AccessTools.Field(typeof(ItemGun), "physGrabObject");
        static readonly FieldInfo ItemGunItemBattery = AccessTools.Field(typeof(ItemGun), "itemBattery");
        static readonly FieldInfo ItemGunGunMuzzle = AccessTools.Field(typeof(ItemGun), "gunMuzzle");
        static readonly FieldInfo ItemGunGunRange = AccessTools.Field(typeof(ItemGun), "gunRange");
        static readonly FieldInfo ItemBatteryLife = AccessTools.Field(typeof(ItemBattery), "batteryLife");
        static readonly FieldInfo ItemBatteryLifePrev = AccessTools.Field(typeof(ItemBattery), "batteryLifePrev");
        static readonly FieldInfo ItemBatteryLifeInt = AccessTools.Field(typeof(ItemBattery), "batteryLifeInt");
        static readonly FieldInfo ItemBatteryBars = AccessTools.Field(typeof(ItemBattery), "batteryBars");
        static readonly FieldInfo ItemEquippableOwnerPlayerId = AccessTools.Field(typeof(ItemEquippable), "ownerPlayerId");
        static readonly MethodInfo ItemEquippableGetOwnerPlayerAvatar = AccessTools.Method(typeof(ItemEquippable), "GetOwnerPlayerAvatar");
        static readonly FieldInfo StatsItemDictionary = AccessTools.Field(typeof(StatsManager), "itemDictionary");
        static readonly FieldInfo ItemPrefab = AccessTools.Field(typeof(Item), "prefab");
        static readonly FieldInfo ItemDisabled = AccessTools.Field(typeof(Item), "disabled");
        static readonly FieldInfo ItemName = AccessTools.Field(typeof(Item), "itemName");
        static readonly MethodInfo InventoryGetFirstFreeSpot = AccessTools.Method(typeof(Inventory), "GetFirstFreeInventorySpotIndex");
        static readonly MethodInfo ItemEquippableRequestEquip = AccessTools.Method(typeof(ItemEquippable), "RequestEquip", new[] { typeof(int), typeof(int) });
        static readonly MethodInfo BatteryFullPercentChange = AccessTools.Method(typeof(ItemBattery), "BatteryFullPercentChange", new[] { typeof(int), typeof(bool) });
        static readonly PropertyInfo PrefabRefPrefab = AccessTools.Property(typeof(PrefabRef), "Prefab");
        static readonly PropertyInfo PrefabRefResourcePath = AccessTools.Property(typeof(PrefabRef), "ResourcePath");
        static readonly FieldInfo ValuableDirectorValuableDebug = AccessTools.Field(typeof(ValuableDirector), "valuableDebug");
        static readonly FieldInfo LevelGeneratorEnemiesSpawnTarget = AccessTools.Field(typeof(LevelGenerator), "EnemiesSpawnTarget");
        static readonly FieldInfo LevelGeneratorEnemiesSpawned = AccessTools.Field(typeof(LevelGenerator), "EnemiesSpawned");
        static readonly FieldInfo LevelGeneratorEnemyReady = AccessTools.Field(typeof(LevelGenerator), "EnemyReady");
        static readonly FieldInfo RunManagerPreviousRunLevel = AccessTools.Field(typeof(RunManager), "previousRunLevel");
        static readonly MethodInfo RunManagerChangeLevel = AccessTools.Method(typeof(RunManager), "ChangeLevel", new[] { typeof(bool), typeof(bool), typeof(RunManager.ChangeLevelType) });
        static readonly FieldInfo PlayerControllerRigidbody = AccessTools.Field(typeof(PlayerController), "rb");
        static readonly FieldInfo PlayerAvatarRigidbody = AccessTools.Field(typeof(PlayerAvatar), "rb");
        static readonly FieldInfo PlayerAvatarClientPosition = AccessTools.Field(typeof(PlayerAvatar), "clientPosition");
        static readonly FieldInfo PlayerAvatarClientPositionCurrent = AccessTools.Field(typeof(PlayerAvatar), "clientPositionCurrent");
        static readonly FieldInfo PlayerAvatarRbVelocity = AccessTools.Field(typeof(PlayerAvatar), "rbVelocity");
        static readonly FieldInfo PlayerAvatarRbVelocityRaw = AccessTools.Field(typeof(PlayerAvatar), "rbVelocityRaw");
        static readonly Type ValuableDebugType = AccessTools.Inner(typeof(ValuableDirector), "ValuableDebug");

        internal static List<PlayerAvatar> PlayerList()
        {
            return GameDirector.instance == null ? null : GameDirectorPlayerList?.GetValue(GameDirector.instance) as List<PlayerAvatar>;
        }

        internal static bool IsMainState()
        {
            if (GameDirector.instance == null || GameDirectorCurrentState == null) return false;
            return (GameDirector.gameState)GameDirectorCurrentState.GetValue(GameDirector.instance) == GameDirector.gameState.Main;
        }

        internal static PlayerHealth Health(PlayerAvatar avatar)
        {
            return avatar == null ? null : PlayerAvatarHealth?.GetValue(avatar) as PlayerHealth;
        }

        internal static int HealthValue(PlayerHealth health)
        {
            if (health == null || PlayerHealthValue == null) return 0;
            try { return (int)PlayerHealthValue.GetValue(health); } catch { return 0; }
        }

        internal static PhysGrabber AvatarGrabber(PlayerAvatar avatar)
        {
            return avatar == null ? null : PlayerAvatarPhysGrabber?.GetValue(avatar) as PhysGrabber;
        }

        internal static bool GetIsCrouching(PlayerAvatar avatar)
        {
            if (avatar == null || PlayerAvatarIsCrouching == null) return false;
            return (bool)PlayerAvatarIsCrouching.GetValue(avatar);
        }

        internal static void SetIsCrouching(PlayerAvatar avatar, bool value)
        {
            if (avatar != null) PlayerAvatarIsCrouching?.SetValue(avatar, value);
        }

        internal static PhysGrabObject GunPhysGrabObject(ItemGun gun)
        {
            return gun == null ? null : ItemGunPhysGrabObject?.GetValue(gun) as PhysGrabObject;
        }

        internal static ItemBattery GunBattery(ItemGun gun)
        {
            return gun == null ? null : ItemGunItemBattery?.GetValue(gun) as ItemBattery ?? gun?.GetComponent<ItemBattery>();
        }

        internal static Transform GunMuzzle(ItemGun gun)
        {
            return gun == null ? null : ItemGunGunMuzzle?.GetValue(gun) as Transform;
        }

        internal static float GunRange(ItemGun gun, float fallback)
        {
            if (gun == null || ItemGunGunRange == null) return fallback;
            return Mathf.Max(1f, Convert.ToSingle(ItemGunGunRange.GetValue(gun)));
        }

        internal static PlayerAvatar GunOwner(ItemGun gun)
        {
            if (gun == null) return null;

            var equippable = gun.GetComponent<ItemEquippable>();
            if (equippable != null)
            {
                try
                {
                    var owner = ItemEquippableGetOwnerPlayerAvatar?.Invoke(equippable, null) as PlayerAvatar;
                    if (owner != null) return owner;
                }
                catch { }

                try
                {
                    int ownerId = ItemEquippableOwnerPlayerId == null ? -1 : (int)ItemEquippableOwnerPlayerId.GetValue(equippable);
                    if (ownerId != -1)
                    {
                        var view = PhotonView.Find(ownerId);
                        var avatar = view != null ? view.GetComponent<PlayerAvatar>() : null;
                        if (avatar != null) return avatar;
                    }
                }
                catch { }
            }

            var physGrabObject = GunPhysGrabObject(gun);
            var lastGrabber = physGrabObject == null ? null : PhysGrabObjectLastPlayerGrabbing?.GetValue(physGrabObject) as PhysGrabber;
            var grabbedAvatar = lastGrabber == null ? null : PhysGrabberPlayerAvatar?.GetValue(lastGrabber) as PlayerAvatar;
            return grabbedAvatar;
        }

        internal static void RefillBattery(ItemGun gun)
        {
            var battery = GunBattery(gun);
            if (battery == null) return;

            int bars = 6;
            if (ItemBatteryBars != null)
            {
                try { bars = Mathf.Max(1, (int)ItemBatteryBars.GetValue(battery)); } catch { }
            }

            ItemBatteryLife?.SetValue(battery, 100f);
            ItemBatteryLifePrev?.SetValue(battery, 100f);
            ItemBatteryLifeInt?.SetValue(battery, bars);
            try { BatteryFullPercentChange?.Invoke(battery, new object[] { bars, false }); } catch { }
        }

        internal static bool IsItemDisabled(Item item)
        {
            if (item == null || ItemDisabled == null) return true;
            return (bool)ItemDisabled.GetValue(item);
        }

        internal static string ItemDisplayName(Item item)
        {
            if (item == null) return "";
            return (ItemName?.GetValue(item) as string) ?? item.name ?? "";
        }

        internal static Dictionary<string, Item> ItemDictionary()
        {
            return StatsManager.instance == null ? null : StatsItemDictionary?.GetValue(StatsManager.instance) as Dictionary<string, Item>;
        }

        internal static PrefabRef ItemPrefabRef(Item item)
        {
            return item == null ? null : ItemPrefab?.GetValue(item) as PrefabRef;
        }

        internal static GameObject LoadPrefab(PrefabRef prefabRef)
        {
            if (prefabRef == null || PrefabRefPrefab == null) return null;
            try { return PrefabRefPrefab.GetValue(prefabRef, null) as GameObject; } catch { return null; }
        }

        internal static string ResourcePath(PrefabRef prefabRef)
        {
            if (prefabRef == null || PrefabRefResourcePath == null) return "";
            try { return PrefabRefResourcePath.GetValue(prefabRef, null) as string ?? ""; } catch { return ""; }
        }

        internal static int FirstFreeInventorySpot()
        {
            if (Inventory.instance == null || InventoryGetFirstFreeSpot == null) return -1;
            try { return (int)InventoryGetFirstFreeSpot.Invoke(Inventory.instance, null); } catch { return -1; }
        }

        internal static void RequestEquip(ItemEquippable equippable, int spot, int physGrabberViewId)
        {
            if (equippable == null || ItemEquippableRequestEquip == null) return;
            try { ItemEquippableRequestEquip.Invoke(equippable, new object[] { spot, physGrabberViewId }); } catch { }
        }

        internal static void SetValuableDebugAll(ValuableDirector director)
        {
            if (director == null || ValuableDirectorValuableDebug == null || ValuableDebugType == null) return;
            try { ValuableDirectorValuableDebug.SetValue(director, Enum.ToObject(ValuableDebugType, 1)); } catch { }
        }

        internal static void SetEnemyGenerationSkipped(LevelGenerator levelGenerator)
        {
            if (levelGenerator == null) return;
            try { LevelGeneratorEnemiesSpawnTarget?.SetValue(levelGenerator, 0); } catch { }
            try { LevelGeneratorEnemiesSpawned?.SetValue(levelGenerator, 0); } catch { }
            try { LevelGeneratorEnemyReady?.SetValue(levelGenerator, true); } catch { }
        }

        internal static void ChangeToRandomRunLevel()
        {
            var run = RunManager.instance;
            if (run == null) return;
            try { RunManagerPreviousRunLevel?.SetValue(run, run.levelCurrent); } catch { }
            try { RunManagerChangeLevel?.Invoke(run, new object[] { false, false, RunManager.ChangeLevelType.RunLevel }); }
            catch
            {
                try { run.ChangeLevel(false, false, RunManager.ChangeLevelType.RunLevel); } catch { }
            }
        }

        internal static void FreezeLocalPlayerPosition(PlayerAvatar avatar, Vector3 avatarPosition, Vector3 controllerPosition)
        {
            var controller = PlayerController.instance;
            if (controller != null)
            {
                controller.transform.position = controllerPosition;
                var rb = PlayerControllerRigidbody?.GetValue(controller) as Rigidbody;
                if (rb != null)
                {
                    rb.position = controllerPosition;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }

            if (avatar != null)
            {
                avatar.transform.position = avatarPosition;
                PlayerAvatarClientPosition?.SetValue(avatar, avatarPosition);
                PlayerAvatarClientPositionCurrent?.SetValue(avatar, avatarPosition);
                PlayerAvatarRbVelocity?.SetValue(avatar, Vector3.zero);
                PlayerAvatarRbVelocityRaw?.SetValue(avatar, Vector3.zero);
                var rb = PlayerAvatarRigidbody?.GetValue(avatar) as Rigidbody;
                if (rb != null)
                {
                    rb.position = avatarPosition;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
        }
    }
}
