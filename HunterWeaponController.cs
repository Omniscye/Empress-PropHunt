using System;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Empress.REPO.PropHunt
{
    public sealed class HunterWeaponController : MonoBehaviour
    {
        public PropHuntManager Manager;
        float _nextTick;

        public void Tick(PlayerAvatar avatar)
        {
            if (Manager == null || avatar == null || !PhotonNetwork.InRoom) return;
            if (!Manager.IsMainState) return;
            if (Time.time < _nextTick) return;

            _nextTick = Time.time + 0.75f;
            Manager.EnsureHunterWeaponForLocalHunter(avatar);
        }
    }

    public partial class PropHuntManager
    {
        int _hunterWeaponViewId = -1;
        float _nextHunterWeaponSpawnAttempt;
        float _nextHunterWeaponRequest;
        float _nextHunterWeaponEquip;
        Item _cachedGunItem;

        internal bool IsActorHunter(int actorNumber) => actorNumber > 0 && actorNumber == _hunterActor;

        internal bool IsActorDisguised(int actorNumber)
        {
            if (actorNumber <= 0 || _deadActors.Contains(actorNumber)) return false;
            var player = PhotonNetwork.CurrentRoom?.GetPlayer(actorNumber);
            if (player == null || !player.CustomProperties.ContainsKey(PLAYER_KEY_PROP)) return false;
            return TryToInt(player.CustomProperties[PLAYER_KEY_PROP], out int propViewId) && propViewId > 0;
        }

        internal int ActorNumber(PlayerAvatar avatar)
        {
            return avatar?.photonView?.Owner?.ActorNumber ?? -1;
        }

        internal void EnsureHunterWeaponForLocalHunter(PlayerAvatar avatar)
        {
            if (_localRole != PHRole.Hunter || avatar == null) return;
            if (!PreHidePhaseActive && !RoundIsLive) return;

            if (TryFindOwnedHunterGun(avatar, out var ownedGun))
            {
                GameAccess.RefillBattery(ownedGun);
                return;
            }

            TryEquipSyncedHunterWeapon(avatar);

            if (TryFindOwnedHunterGun(avatar, out ownedGun))
            {
                GameAccess.RefillBattery(ownedGun);
                return;
            }

            if (PhotonNetwork.IsMasterClient)
            {
                TrySpawnHunterWeapon(avatar);
                return;
            }

            if (Time.time >= _nextHunterWeaponRequest)
            {
                _nextHunterWeaponRequest = Time.time + 2f;
                var content = new object[] { EVT_TAG, PhotonNetwork.LocalPlayer.ActorNumber };
                PhotonNetwork.RaiseEvent(EVT_HUNTER_GUN_REQUEST, content, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
            }
        }

        internal bool HandleHunterWeaponEvent(EventData e)
        {
            if (e.Code != EVT_HUNTER_GUN_REQUEST && e.Code != EVT_HUNTER_GUN_SYNC) return false;
            if (!(e.CustomData is object[] arr) || arr.Length < 2) return true;
            if (!(arr[0] is string tag) || tag != EVT_TAG) return true;

            if (e.Code == EVT_HUNTER_GUN_REQUEST)
            {
                if (!PhotonNetwork.IsMasterClient) return true;
                if (!TryToInt(arr[1], out int actorNumber) || actorNumber != _hunterActor) return true;
                var avatar = FindAvatarByActor(actorNumber);
                if (avatar != null) TrySpawnHunterWeapon(avatar);
                return true;
            }

            if (TryToInt(arr[1], out int viewId))
                _hunterWeaponViewId = viewId;

            return true;
        }

        internal bool TryResolveHunterGunHit(ItemGun gun)
        {
            if (gun == null || !PhotonNetwork.InRoom) return false;
            if (!RoundIsLive || IsHunterLocked) return false;
            if (PhotonNetwork.IsMasterClient == false) return false;

            var shooter = GameAccess.GunOwner(gun);
            int shooterActor = ActorNumber(shooter);
            if (!IsActorHunter(shooterActor)) return false;

            var muzzle = GameAccess.GunMuzzle(gun);
            Transform originTransform = muzzle != null ? muzzle : gun.transform;
            var ray = new Ray(originTransform.position, originTransform.forward);
            float range = Mathf.Max(GunRangeMeters, GameAccess.GunRange(gun, GunRangeMeters));
            int targetActor = -1;
            float bestDistance = range;

            var players = GameAccess.PlayerList();
            if (players == null) return false;

            foreach (var avatar in players)
            {
                int actor = ActorNumber(avatar);
                if (actor <= 0 || actor == shooterActor || !IsActorDisguised(actor)) continue;

                var disguise = avatar.GetComponent<PropDisguiseController>();
                if (disguise == null || !disguise.TryRaycastDisguise(ray, bestDistance, out float distance)) continue;
                if (ShotBlocked(ray, distance, avatar)) continue;

                bestDistance = distance;
                targetActor = actor;
            }

            if (targetActor <= 0) return false;
            RequestEliminateFromActor(shooterActor, targetActor);
            return true;
        }

        internal bool TryApplyHunterShotCost(ItemGun gun)
        {
            if (gun == null || !PhotonNetwork.InRoom) return false;
            if (!RoundIsLive || IsHunterLocked) return false;
            if (PhotonNetwork.IsMasterClient == false) return false;

            var shooter = GameAccess.GunOwner(gun);
            int shooterActor = ActorNumber(shooter);
            if (!IsActorHunter(shooterActor)) return false;

            var health = GameAccess.Health(shooter);
            if (health == null) return false;

            try
            {
                int healthBeforeShot = GameAccess.HealthValue(health);
                health.HurtOther(5, shooter.transform.position, false, -1);
                return healthBeforeShot > 5;
            }
            catch { return false; }
        }

        void RequestEliminateFromActor(int shooterActor, int targetActor)
        {
            var content = new object[] { EVT_TAG, shooterActor, targetActor };
            PhotonNetwork.RaiseEvent(EVT_KILL, content, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        }

        bool ShotBlocked(Ray ray, float targetDistance, PlayerAvatar targetAvatar)
        {
            try
            {
                int mask = SemiFunc.LayerMaskGetVisionObstruct();
                if (!Physics.Raycast(ray, out var hit, Mathf.Max(0.01f, targetDistance - 0.04f), mask, QueryTriggerInteraction.Ignore))
                    return false;

                var hitAvatar = hit.collider != null ? hit.collider.GetComponentInParent<PlayerAvatar>() : null;
                return hitAvatar == null || hitAvatar != targetAvatar;
            }
            catch
            {
                return false;
            }
        }

        bool TryFindOwnedHunterGun(PlayerAvatar avatar, out ItemGun gun)
        {
            gun = null;
            var all = UnityEngine.Object.FindObjectsOfType<ItemGun>();
            foreach (var itemGun in all)
            {
                if (itemGun == null) continue;
                var owner = GameAccess.GunOwner(itemGun);
                if (owner != avatar) continue;
                gun = itemGun;
                return true;
            }
            return false;
        }

        void TryEquipSyncedHunterWeapon(PlayerAvatar avatar)
        {
            if (Time.time < _nextHunterWeaponEquip || _hunterWeaponViewId <= 0) return;
            _nextHunterWeaponEquip = Time.time + 1f;

            var view = PhotonView.Find(_hunterWeaponViewId);
            if (view == null) return;

            var gun = view.GetComponent<ItemGun>();
            if (gun == null) return;
            GameAccess.RefillBattery(gun);

            var physGrabObject = GameAccess.GunPhysGrabObject(gun);
            var grabber = GameAccess.AvatarGrabber(avatar) ?? PhysGrabber.instance;
            if (physGrabObject != null && grabber != null)
            {
                try
                {
                    grabber.OverrideGrab(physGrabObject, 0.2f);
                    grabber.OverrideGrabDistance(0.8f);
                }
                catch { }
            }

            var equippable = gun.GetComponent<ItemEquippable>();
            if (equippable == null || grabber == null || grabber.photonView == null) return;

            int spot = GameAccess.FirstFreeInventorySpot();
            if (spot < 0) return;
            GameAccess.RequestEquip(equippable, spot, grabber.photonView.ViewID);
        }

        bool TrySpawnHunterWeapon(PlayerAvatar avatar)
        {
            if (avatar == null || Time.time < _nextHunterWeaponSpawnAttempt) return false;
            _nextHunterWeaponSpawnAttempt = Time.time + 1.5f;

            if (_hunterWeaponViewId > 0 && PhotonView.Find(_hunterWeaponViewId) != null) return true;
            if (!TryFindHunterGunItem(out var item, out string resourcePath)) return false;

            Vector3 position = avatar.transform.position + avatar.transform.forward * 0.75f + Vector3.up * 1.05f;
            Quaternion rotation = Quaternion.LookRotation(avatar.transform.forward, Vector3.up);
            GameObject spawned = null;

            try { spawned = PhotonNetwork.InstantiateRoomObject(resourcePath, position, rotation, 0); }
            catch (Exception ex)
            {
                LogGate.WarnThrottled(PropHuntPlugin.Log, "[Empress Prop Hunt] Hunter handgun spawn failed: " + ex.Message);
                return false;
            }

            var view = spawned != null ? spawned.GetComponent<PhotonView>() : null;
            if (view == null) return false;

            _hunterWeaponViewId = view.ViewID;
            var gun = spawned.GetComponent<ItemGun>();
            if (gun != null) GameAccess.RefillBattery(gun);

            var content = new object[] { EVT_TAG, _hunterWeaponViewId };
            PhotonNetwork.RaiseEvent(EVT_HUNTER_GUN_SYNC, content, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
            PropHuntPlugin.Log.LogInfo("[Empress Prop Hunt] Hunter handgun spawned.");
            return true;
        }

        bool TryFindHunterGunItem(out Item item, out string resourcePath)
        {
            item = null;
            resourcePath = "";

            if (_cachedGunItem != null)
            {
                var cachedRef = GameAccess.ItemPrefabRef(_cachedGunItem);
                resourcePath = GameAccess.ResourcePath(cachedRef);
                item = _cachedGunItem;
                if (!string.IsNullOrEmpty(resourcePath)) return true;
            }

            var dictionary = GameAccess.ItemDictionary();
            if (dictionary == null || dictionary.Count == 0) return false;

            int bestScore = int.MinValue;
            foreach (var pair in dictionary)
            {
                var candidate = pair.Value;
                if (candidate == null || GameAccess.IsItemDisabled(candidate)) continue;

                var prefabRef = GameAccess.ItemPrefabRef(candidate);
                var prefab = GameAccess.LoadPrefab(prefabRef);
                if (prefab == null || prefab.GetComponentInChildren<ItemGun>(true) == null) continue;

                string name = (pair.Key + " " + GameAccess.ItemDisplayName(candidate) + " " + prefab.name).ToLowerInvariant();
                int score = 0;
                if (name.Contains("handgun")) score += 200;
                if (name.Contains("pistol")) score += 180;
                if (name.Contains("gun")) score += 120;
                if (name.Contains("weapon")) score += 30;
                if (name.Contains("laser") || name.Contains("orb") || name.Contains("staff")) score -= 100;

                if (score <= bestScore) continue;

                string path = GameAccess.ResourcePath(prefabRef);
                if (string.IsNullOrEmpty(path)) continue;

                bestScore = score;
                item = candidate;
                resourcePath = path;
            }

            if (item == null) return false;
            _cachedGunItem = item;
            return true;
        }
    }
}
