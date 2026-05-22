using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Empress.REPO.PropHunt
{
    [BepInPlugin("empress.repo.prophunt", "Empress Prop Hunt", "2.0.0")]
    public class PropHuntPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Harmony Harmony;
        internal static ConfigEntry<float> CfgPreHideSeconds;
        internal static ConfigEntry<float> CfgHunterLockSeconds;
        internal static ConfigEntry<float> CfgRoundSeconds;

        internal static ConfigEntry<float> CfgGunRangeMeters;

        internal static ConfigEntry<KeyCode> CfgKeyDisguiseToggle;
        internal static ConfigEntry<KeyCode> CfgKeyViewToggle;
        internal static ConfigEntry<KeyCode> CfgKeyPositionLock;
        internal static ConfigEntry<bool> CfgDisableEnemies;
        internal static ConfigEntry<bool> CfgMaxOutValuables;

        void Awake()
        {
            Log = Logger;
            gameObject.transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);
            Harmony = new Harmony("empress.repo.prophunt");
            CfgPreHideSeconds = Config.Bind("Gameplay", "PreHideSeconds", 30f, "Seconds of hide time before the round starts.");
            CfgHunterLockSeconds = Config.Bind("Gameplay", "HunterLockSeconds", 30f, "Seconds the Hunter is locked during pre-hide.");
            CfgRoundSeconds = Config.Bind("Gameplay", "RoundSeconds", 300f, "Live round duration in seconds.");

            CfgGunRangeMeters = Config.Bind("Hunter", "GunRangeMeters", 60f, "Hunter handgun hit range in meters.");

            CfgKeyDisguiseToggle = Config.Bind("Controls", "HiderDisguiseKey", KeyCode.V, "Hider: toggle disguise.");
            CfgKeyViewToggle = Config.Bind("Controls", "HiderViewToggleKey", KeyCode.C, "Hider: toggle first/third person.");
            CfgKeyPositionLock = Config.Bind("Controls", "HiderPositionLockKey", KeyCode.X, "Hider: lock position while disguised.");
            CfgDisableEnemies = Config.Bind("Round Rules", "DisableEnemies", true, "Disable enemy spawns during Prop Hunt rounds.");
            CfgMaxOutValuables = Config.Bind("Round Rules", "MaxOutValuables", true, "Spawn more valuables for hider choices.");

            Harmony.PatchAll();

            SceneManager.sceneLoaded += OnSceneLoaded;
            StartCoroutine(LevelWatcher());
            Log.LogInfo("[Empress Prop Hunt] Loaded v2.0.0.");
        }

        void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) { }

        IEnumerator LevelWatcher()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.5f);

                if (!SemiFunc.IsMultiplayer()) continue;
                if (!PhotonNetwork.InRoom) continue;
                if (SemiFunc.MenuLevel()) continue;
                if (LevelGenerator.Instance == null || GameDirector.instance == null) continue;

                var players = GameAccess.PlayerList();
                bool playersReady = players != null && players.Count > 0;
                bool mainState = GameAccess.IsMainState();

                if (playersReady && mainState && PropHuntManager.Instance == null)
                {
                    var go = new GameObject("PropHuntManager");
                    go.transform.parent = null;
                    go.hideFlags = HideFlags.HideAndDontSave;
                    DontDestroyOnLoad(go);
                    var mgr = go.AddComponent<PropHuntManager>();

                    mgr.PreHideSeconds = Mathf.Max(1f, CfgPreHideSeconds.Value);
                    mgr.HunterLockSeconds = Mathf.Max(0f, CfgHunterLockSeconds.Value);
                    mgr.RoundSeconds = Mathf.Max(5f, CfgRoundSeconds.Value);

                    mgr.GunRangeMeters = Mathf.Max(1f, CfgGunRangeMeters.Value);

                    mgr.KeyHiderToggle = CfgKeyDisguiseToggle.Value;
                    mgr.KeyDisguiseViewToggle = CfgKeyViewToggle.Value;
                    mgr.KeyPositionLock = CfgKeyPositionLock.Value;

                    Log.LogInfo("[Empress Prop Hunt] Manager created.");
                }
            }
        }
    }

    public enum PHRole { None = 0, Hunter = 1, Hider = 2 }
    public enum PHWinner { None = 0, Hunter = 1, Hiders = 2, Draw = 3 }

    static class LogGate
    {
        static float _nextWarnTime;
        public static void WarnThrottled(ManualLogSource log, string msg, float everySeconds = 2f)
        {
            if (Time.realtimeSinceStartup < _nextWarnTime) return;
            _nextWarnTime = Time.realtimeSinceStartup + everySeconds;
            log.LogWarning(msg);
        }
    }

    public partial class PropHuntManager : MonoBehaviourPunCallbacks, IOnEventCallback
    {
        public static PropHuntManager Instance { get; private set; }

        private const string ROOM_KEY_ACTIVE = "PH_Active";
        private const string ROOM_KEY_HUNTER = "PH_Hunter";
        private const string PLAYER_KEY_PROP = "PH_PropVID";
        private const string PLAYER_KEY_LOCKED = "PH_Locked";

        private const byte EVT_KILL = 101;
        private const byte EVT_PLAYERDOWN = 102;
        private const byte EVT_MATCHEND = 103;
        private const byte EVT_HUNTER_GUN_REQUEST = 105;
        private const byte EVT_HUNTER_GUN_SYNC = 106;
        private const string EVT_TAG = "PH1";

        public KeyCode KeyHiderToggle = KeyCode.V;
        public KeyCode KeyDisguiseViewToggle = KeyCode.C;
        public KeyCode KeyPositionLock = KeyCode.X;

        public float HunterLockSeconds = 30f;
        public float PreHideSeconds = 30f;
        public float RoundSeconds = 300f;

        public float GunRangeMeters = 60f;

        private double _roundEndRealtime;
        private bool _roundArmed;

        private bool _hudShown;
        private PHRole _localRole = PHRole.None;
        private int _hunterActor = -1;

        private HunterWeaponController _gun;

        private readonly HashSet<int> _deadActors = new HashSet<int>();
        private bool _ending;
        private float _endUntil;
        private string _endMsg = "";
        private PHWinner _whoWon = PHWinner.None;
        private float _hunterLockUntil = 0f;
        public bool IsHunterLocked => _localRole == PHRole.Hunter && Time.time < _hunterLockUntil;
        internal const float TINY_FACTOR = 0.0001f;
        private int _cachedPropPhotonViewId = 0;
        private float _preHideUntil = 0f;
        private bool _preHideArmed = false;
        private bool PreHideActive => _preHideArmed && Time.time < _preHideUntil;
        private bool _positionLocked;
        private Vector3 _positionLockAvatar;
        private Vector3 _positionLockController;
        public bool IsMainState => GameAccess.IsMainState();
        public bool PreHidePhaseActive => PreHideActive;
        public bool RoundIsLive => _roundArmed && Time.time < _roundEndRealtime;

        void Awake()
        {
            Instance = this;
            gameObject.transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(gameObject);
        }

        new void OnEnable()
        {
            PhotonNetwork.AddCallbackTarget(this);
            SceneManager.sceneLoaded += OnSceneLoaded_ResetOrDie;
        }

        new void OnDisable()
        {
            PhotonNetwork.RemoveCallbackTarget(this);
            SceneManager.sceneLoaded -= OnSceneLoaded_ResetOrDie;
        }

        void OnSceneLoaded_ResetOrDie(Scene scene, LoadSceneMode mode)
        {
            bool inGameplay = !SemiFunc.MenuLevel() &&
                              LevelGenerator.Instance != null &&
                              GameDirector.instance != null;

            HardResetUI(clearRole: !inGameplay);

            if (!inGameplay)
            {
                DeactivateManager("scene changed to menu/lobby");
                return;
            }
            try
            {
                PhotonNetwork.LocalPlayer?.SetCustomProperties(
                    new ExitGames.Client.Photon.Hashtable { { PLAYER_KEY_PROP, 0 }, { PLAYER_KEY_LOCKED, 0 } });
            }
            catch { }

            ApplyRoomState(PhotonNetwork.CurrentRoom?.CustomProperties);
            try
            {
                EnsureControllersOnAllAvatars();
                SyncAllPlayerDisguises();
            }
            catch { }
            if (!_preHideArmed)
                StartCoroutine(ArmPreHideWhenGameplayReady());
        }
        void Start()
        {
            if (PhotonNetwork.IsMasterClient)
            {
                var rp = PhotonNetwork.CurrentRoom.CustomProperties;
                if (!rp.ContainsKey(ROOM_KEY_ACTIVE))
                {
                    var candidates = PhotonNetwork.PlayerList.Where(p => !p.IsInactive).ToList();
                    if (candidates.Count > 0)
                    {
                        var pick = candidates[new System.Random().Next(candidates.Count)];
                        PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable {
                            { ROOM_KEY_ACTIVE, 1 }, { ROOM_KEY_HUNTER, pick.ActorNumber }});
                        PropHuntPlugin.Log.LogInfo($"[Empress Prop Hunt] Master picked Hunter actor {pick.ActorNumber}.");
                    }
                }
            }

            ApplyRoomState(PhotonNetwork.CurrentRoom?.CustomProperties);
            EnsureControllersOnAllAvatars();
            SyncAllPlayerDisguises();

            if (!_preHideArmed)
                StartCoroutine(ArmPreHideWhenGameplayReady());
        }
        IEnumerator ArmPreHideWhenGameplayReady()
        {
            while (!GameAccess.IsMainState())
                yield return null;
            while (GameAccess.PlayerList() == null ||
                   GameAccess.PlayerList().Count == 0)
                yield return null;
            yield return new WaitForSeconds(0.25f);
            _preHideArmed = true;
            _preHideUntil = Time.time + Mathf.Max(1f, PreHideSeconds);
            if (_localRole == PHRole.Hunter)
            {
                _hunterLockUntil = Time.time + Mathf.Max(1f, HunterLockSeconds);
                Flash($"Hunter locked for {Mathf.CeilToInt(HunterLockSeconds)}s.");
            }
            if (PhotonNetwork.IsMasterClient)
                StartCoroutine(HostHealPassesDuringAndAfterPrehide());
        }
        IEnumerator HostHealPassesDuringAndAfterPrehide()
        {
            HostTopOffAll();
            yield return new WaitForSeconds(Mathf.Max(0.25f, PreHideSeconds * 0.5f));
            HostTopOffAll();
            while (PreHideActive) yield return null;
            HostTopOffAll();
            yield return new WaitForSeconds(0.75f);
            HostTopOffAll();
        }

        void HostTopOffAll()
        {
            try
            {
                var list = GameAccess.PlayerList();
                if (list == null || list.Count == 0) return;

                foreach (var av in list)
                {
                    var health = GameAccess.Health(av);
                    if (av == null || health == null) continue;
                    try { health.HealOther(health.maxHealth, true); }
                    catch (Exception ex1)
                    {
                        LogGate.WarnThrottled(PropHuntPlugin.Log, $"[Empress Prop Hunt] Heal failed for actor {(av?.photonView?.Owner?.ActorNumber ?? -1)}: {ex1.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogGate.WarnThrottled(PropHuntPlugin.Log, $"[Empress Prop Hunt] HostTopOffAll guarded: {ex.Message}");
            }
        }
        IEnumerator HostHealPlayerWhenReady(int actorNumber)
        {
            if (!PhotonNetwork.IsMasterClient) yield break;

            float t = 4f;
            while (t > 0f)
            {
                var av = FindAvatarByActor(actorNumber);
                var health = GameAccess.Health(av);
                if (av != null && health != null)
                {
                    try { health.HealOther(health.maxHealth, true); } catch { }
                    yield return new WaitForSeconds(0.5f);
                    try { health.HealOther(health.maxHealth, true); } catch { }
                    yield break;
                }
                t -= Time.deltaTime;
                yield return null;
            }
        }

        void HardResetUI(bool clearRole)
        {
            _ending = false;
            _endMsg = "";
            _whoWon = PHWinner.None;
            _hudShown = false;
            _flashMsg = ""; _flashMsgUntil = 0f;

            _roundArmed = false;
            _roundEndRealtime = 0;
            _deadActors.Clear();
            _gun = null;

            _hunterLockUntil = 0f;

            _preHideArmed = false;
            _preHideUntil = 0f;
            _positionLocked = false;
            _positionLockAvatar = Vector3.zero;
            _positionLockController = Vector3.zero;

            if (clearRole) _localRole = PHRole.None;

            _cachedPropPhotonViewId = 0;
        }

        void DeactivateManager(string why)
        {
            try
            {
                if (Instance == this)
                    Instance = null;
                enabled = false;
                PropHuntPlugin.Log.LogInfo("[Empress Prop Hunt] Manager stopped (" + why + ").");
            }
            catch { }
        }

        public override void OnLeftRoom()
        {
            HardResetUI(clearRole: true);
            DeactivateManager("left room");
        }

        public override void OnDisconnected(DisconnectCause cause)
        {
            HardResetUI(clearRole: true);
            DeactivateManager("disconnected");
        }

        public override void OnJoinedRoom()
        {
            ApplyRoomState(PhotonNetwork.CurrentRoom?.CustomProperties);
            EnsureControllersOnAllAvatars();
            SyncAllPlayerDisguises();
            if (!_preHideArmed) StartCoroutine(ArmPreHideWhenGameplayReady());
        }

        public override void OnPlayerEnteredRoom(Player newPlayer)
        {
            StartCoroutine(ApplyDisguiseForPlayerWhenReady(newPlayer));
            StartCoroutine(HostHealPlayerWhenReady(newPlayer.ActorNumber));
        }

        IEnumerator ApplyDisguiseForPlayerWhenReady(Player p)
        {
            float t = 3f;
            while (t > 0f)
            {
                var av = FindAvatarByActor(p.ActorNumber);
                if (av != null) { ApplyOnePlayerDisguise(p, av); yield break; }
                t -= Time.deltaTime;
                yield return null;
            }
        }

        void Update()
        {
            if (SemiFunc.MenuLevel() || LevelGenerator.Instance == null || GameDirector.instance == null) return;
            if (!PhotonNetwork.InRoom || _localRole == PHRole.None) return;

            RefreshConfigValues();

            if (_ending)
            {
                TryLockControls();
                if (PhotonNetwork.IsMasterClient && Time.time >= _endUntil)
                {
                    _ending = false;
                    SafeAdvanceScene();
                }
                return;
            }

            var me = FindLocalAvatar();
            if (me == null) return;

            if (!_hudShown) { StartCoroutine(HUDBurst()); _hudShown = true; }
            if (!_roundArmed && _preHideArmed && !PreHideActive)
            {
                _roundArmed = true;
                _roundEndRealtime = Time.time + Mathf.Max(5f, RoundSeconds);
            }

            if (PhotonNetwork.IsMasterClient && _roundArmed && Time.time >= _roundEndRealtime)
            {
                _roundArmed = false;
                BroadcastMatchEnd(PHWinner.Hiders, "Time expired");
            }

            if (_localRole == PHRole.Hider)
            {
                var ctrl = me.GetComponent<PropDisguiseController>();
                if (ctrl == null || !ctrl.IsDisguised)
                    UpdateCachedTargetFromPhysGrab();

                if (Input.GetKeyDown(KeyHiderToggle)) ToggleDisguise(me);
                if (Input.GetKeyDown(KeyPositionLock)) TogglePositionLock(me);

                if (Input.GetKeyDown(KeyDisguiseViewToggle))
                {
                    if (ctrl != null && ctrl.IsDisguised)
                    {
                        bool third = ctrl.ToggleDisguiseView();
                        Flash(third ? "View: third-person" : "View: first-person");
                    }
                }

                TickPositionLock(me);
            }
            else if (_localRole == PHRole.Hunter)
            {
                if (_gun == null) _gun = me.gameObject.GetComponent<HunterWeaponController>() ?? me.gameObject.AddComponent<HunterWeaponController>();
                _gun.Manager = this;
                _gun.Tick(me);

                if (IsHunterLocked)
                {
                    try
                    {
                        var pc = PlayerController.instance;
                        if (pc != null) pc.OverrideSpeed(0f, 0.2f);
                    }
                    catch { }
                }
            }
        }

        void RefreshConfigValues()
        {
            if (PropHuntPlugin.CfgKeyDisguiseToggle != null) KeyHiderToggle = PropHuntPlugin.CfgKeyDisguiseToggle.Value;
            if (PropHuntPlugin.CfgKeyViewToggle != null) KeyDisguiseViewToggle = PropHuntPlugin.CfgKeyViewToggle.Value;
            if (PropHuntPlugin.CfgKeyPositionLock != null) KeyPositionLock = PropHuntPlugin.CfgKeyPositionLock.Value;
            if (PropHuntPlugin.CfgGunRangeMeters != null) GunRangeMeters = Mathf.Max(1f, PropHuntPlugin.CfgGunRangeMeters.Value);
        }

        void TogglePositionLock(PlayerAvatar me)
        {
            if (_positionLocked)
            {
                _positionLocked = false;
                SetLocalVisualLock(me, false);
                SetLocalLockProperty(false);
                Flash("Position unlocked.");
                return;
            }

            var ctrl = me != null ? me.GetComponent<PropDisguiseController>() : null;
            if (ctrl == null || !ctrl.IsDisguised)
            {
                Flash("Disguise first.");
                return;
            }

            _positionLocked = true;
            _positionLockAvatar = me.transform.position;
            _positionLockController = PlayerController.instance != null ? PlayerController.instance.transform.position : _positionLockAvatar;
            ctrl.SetVisualPoseLocked(true);
            SetLocalLockProperty(true);
            Flash("Position locked.");
        }

        void TickPositionLock(PlayerAvatar me)
        {
            if (!_positionLocked) return;

            var ctrl = me != null ? me.GetComponent<PropDisguiseController>() : null;
            if (me == null || ctrl == null || !ctrl.IsDisguised || _localRole != PHRole.Hider)
            {
                _positionLocked = false;
                SetLocalVisualLock(me, false);
                SetLocalLockProperty(false);
                return;
            }

            try
            {
                var pc = PlayerController.instance;
                if (pc != null) pc.OverrideSpeed(0f, 0.25f);
                GameAccess.FreezeLocalPlayerPosition(me, _positionLockAvatar, _positionLockController);
            }
            catch { }
        }

        void SetLocalVisualLock(PlayerAvatar me, bool locked)
        {
            try
            {
                var ctrl = me != null ? me.GetComponent<PropDisguiseController>() : null;
                if (ctrl != null) ctrl.SetVisualPoseLocked(locked);
            }
            catch { }
        }

        void SetLocalLockProperty(bool locked)
        {
            try
            {
                PhotonNetwork.LocalPlayer?.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { PLAYER_KEY_LOCKED, locked ? 1 : 0 } });
            }
            catch { }
        }

        void BroadcastMatchEnd(PHWinner winner, string reason)
        {
            var content = new object[] { EVT_TAG, (int)winner, reason ?? "" };
            PhotonNetwork.RaiseEvent(EVT_MATCHEND, content, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
            StartEndBannerLocal(winner, reason);
        }

        void StartEndBannerLocal(PHWinner winner, string reason)
        {
            _whoWon = winner;
            _endMsg = winner == PHWinner.Hunter ? "HUNTER WINS!" :
                      winner == PHWinner.Hiders ? "HIDERS WIN!" : "DRAW";
            if (!string.IsNullOrEmpty(reason)) _endMsg += $"  ({reason})";
            _ending = true;
            _endUntil = Time.time + 10f;
            TryLockControls();
        }

        void RepickHunterForNextRound()
        {
            if (!PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom) return;

            var active = PhotonNetwork.PlayerList.Where(p => !p.IsInactive).ToList();
            if (active.Count == 0) return;

            var choices = (active.Count > 1)
                ? active.Where(p => p.ActorNumber != _hunterActor).ToList()
                : active;

            if (choices.Count == 0) choices = active;
            int nextHunter = choices[new System.Random().Next(choices.Count)].ActorNumber;

            PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable {
                { ROOM_KEY_ACTIVE, 1 },
                { ROOM_KEY_HUNTER, nextHunter }
            });

            PropHuntPlugin.Log.LogInfo($"[Empress Prop Hunt] Next-round hunter: {nextHunter} (was {_hunterActor}).");
        }

        void SafeAdvanceScene()
        {
            if (!PhotonNetwork.IsMasterClient) return;

            RepickHunterForNextRound();
            ClearDisguisesBeforeLevelChange();
            GameAccess.ChangeToRandomRunLevel();
        }

        void ClearDisguisesBeforeLevelChange()
        {
            try
            {
                foreach (var player in PhotonNetwork.PlayerList)
                    player.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { PLAYER_KEY_PROP, 0 }, { PLAYER_KEY_LOCKED, 0 } });
            }
            catch { }

            try
            {
                var list = GameAccess.PlayerList();
                if (list == null) return;
                foreach (var avatar in list)
                {
                    if (avatar == null) continue;
                    var ctrl = avatar.GetComponent<PropDisguiseController>();
                    if (ctrl != null) ctrl.ApplyClearLocal();
                }
            }
            catch { }
        }

        void TryLockControls()
        {
            try
            {
                var pc = PlayerController.instance;
                if (pc != null)
                {
                    pc.InputDisable(0.25f);
                    pc.OverrideSpeed(0f, 0.3f);
                    pc.OverrideLookSpeed(0.2f, 0.15f, 0.15f, 0.3f);
                }
            }
            catch { }
        }

        IEnumerator HUDBurst()
        {
            float t = 1.5f;
            while (t > 0f) { t -= Time.deltaTime; yield return null; }
        }

        void OnGUI()
        {
            if (SemiFunc.MenuLevel() || LevelGenerator.Instance == null || GameDirector.instance == null) return;
            if (!PhotonNetwork.InRoom || _localRole == PHRole.None) return;

            GUI.depth = 0;
            var rect = new Rect(20, 20, 720, 180);
            string roleTxt = _localRole == PHRole.Hunter
                ? "EMPRESS PROP HUNT - HUNTER\nShoot disguised valuables with the hunter handgun."
                : $"PROP HUNT — HIDER\n{KeyHiderToggle}: Toggle disguise  |  {KeyDisguiseViewToggle}: Toggle view  |  {KeyPositionLock}: Lock position";
            if (!_ending) GUI.Box(rect, roleTxt);

            float infoY = rect.yMax + 6;
            if (_localRole == PHRole.Hider && !_ending && PreHideActive)
            {
                float remain = Mathf.Max(0f, _preHideUntil - Time.time);
                int mm = Mathf.FloorToInt(remain / 60f);
                int ss = Mathf.FloorToInt(remain % 60f);
                GUI.Label(new Rect(rect.x + 8, infoY, rect.width, 24), $"Hide time: {mm:00}:{ss:00}");
                infoY += 22f;
            }
            if (_roundArmed && !_ending)
            {
                float remain = Mathf.Max(0f, (float)(_roundEndRealtime - Time.time));
                int mm = Mathf.FloorToInt(remain / 60f);
                int ss = Mathf.FloorToInt(remain % 60f);
                GUI.Label(new Rect(rect.x + 8, infoY, rect.width, 24), $"Time left: {mm:00}:{ss:00}");
                infoY += 22f;
            }
            if (_localRole == PHRole.Hunter && !_ending && IsHunterLocked)
            {
                int lockRemain = Mathf.CeilToInt(_hunterLockUntil - Time.time);
                GUI.Label(new Rect(rect.x + 8, infoY, rect.width, 24), $"Hunter lock: {lockRemain}s");
                infoY += 22f;
            }
            if (_localRole == PHRole.Hider && !_ending)
            {
                string tgt = _cachedPropPhotonViewId != 0 && PhotonView.Find(_cachedPropPhotonViewId) != null
                    ? "Target locked"
                    : "Look at a Valuable to lock target";
                GUI.Label(new Rect(rect.x + 8, infoY, rect.width, 24), $"Disguise: {tgt}");
                infoY += 22f;
                if (_positionLocked)
                {
                    GUI.Label(new Rect(rect.x + 8, infoY, rect.width, 24), $"Position locked ({KeyPositionLock} to release)");
                    infoY += 22f;
                }
            }

            if (Time.time < _flashMsgUntil)
                GUI.Label(new Rect(30, infoY + 8, rect.width, 24), _flashMsg);

            if (_ending)
            {
                string countdown = PhotonNetwork.IsMasterClient
                    ? Mathf.CeilToInt(Mathf.Max(0f, _endUntil - Time.time)).ToString()
                    : "…";

                var full = new Rect(0, 0, Screen.width, Screen.height);
                GUI.color = new Color(0, 0, 0, 0.55f);
                GUI.DrawTexture(full, Texture2D.whiteTexture);
                GUI.color = Color.white;

                var style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = Mathf.RoundToInt(Mathf.Clamp(Screen.width * 0.06f, 32f, 72f)),
                    wordWrap = false
                };
                style.normal.textColor = Color.white;

                GUI.Label(new Rect(0, Screen.height * 0.35f, Screen.width, 80f), _endMsg, style);

                var style2 = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = Mathf.RoundToInt(Mathf.Clamp(Screen.width * 0.03f, 18f, 36f)),
                    wordWrap = false
                };
                style2.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
                GUI.Label(new Rect(0, Screen.height * 0.35f + 68f, Screen.width, 60f),
                          "Next round starting in " + countdown, style2);
            }
        }

        string _flashMsg = ""; float _flashMsgUntil = 0f;
        internal void Flash(string msg, float dur = 1.5f) { _flashMsg = msg; _flashMsgUntil = Time.time + dur; }

        public override void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable changed) => ApplyRoomState(changed);

        public override void OnPlayerPropertiesUpdate(Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps)
        {
            if (!changedProps.ContainsKey(PLAYER_KEY_PROP) && !changedProps.ContainsKey(PLAYER_KEY_LOCKED)) return;

            var avatar = FindAvatarByActor(targetPlayer.ActorNumber);
            if (avatar == null) return;

            var ctrl = avatar.GetComponent<PropDisguiseController>() ?? avatar.gameObject.AddComponent<PropDisguiseController>();

            if (changedProps.ContainsKey(PLAYER_KEY_PROP) && TryToInt(changedProps[PLAYER_KEY_PROP], out int propVid))
            {
                if (propVid == 0 || PhotonView.Find(propVid) == null)
                    ctrl.ApplyClearLocal();
                else
                    ctrl.ApplyDisguiseLocal(propVid, TINY_FACTOR);
            }

            bool locked = targetPlayer.CustomProperties.ContainsKey(PLAYER_KEY_LOCKED) &&
                          TryToInt(targetPlayer.CustomProperties[PLAYER_KEY_LOCKED], out int lockValue) &&
                          lockValue == 1;
            ctrl.SetVisualPoseLocked(locked);
        }

        public override void OnPlayerLeftRoom(Player otherPlayer)
        {
            if (PhotonNetwork.IsMasterClient && otherPlayer != null && otherPlayer.ActorNumber == _hunterActor)
            {
                var list = PhotonNetwork.PlayerList.Where(p => !p.IsInactive).ToList();
                if (list.Count > 0)
                {
                    var pick = list[new System.Random().Next(list.Count)];
                    PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { ROOM_KEY_HUNTER, pick.ActorNumber } });
                    PropHuntPlugin.Log.LogInfo($"[Empress Prop Hunt] Hunter left; new hunter {pick.ActorNumber}.");
                }
            }
        }

        void ApplyRoomState(ExitGames.Client.Photon.Hashtable props)
        {
            if (props == null || !props.ContainsKey(ROOM_KEY_ACTIVE)) return;

            bool active = (int)props[ROOM_KEY_ACTIVE] == 1;
            int hunter = props.ContainsKey(ROOM_KEY_HUNTER) ? (int)props[ROOM_KEY_HUNTER] : -1;

            _hunterActor = hunter;
            var me = PhotonNetwork.LocalPlayer;
            _localRole = (!active) ? PHRole.None : (me.ActorNumber == hunter ? PHRole.Hunter : PHRole.Hider);

            PropHuntPlugin.Log.LogInfo($"[Empress Prop Hunt] Role set: {_localRole} (hunter actor={hunter}).");

            if (_localRole == PHRole.Hunter)
            {
                var myAv = FindLocalAvatar();
                if (myAv != null)
                {
                    _gun = myAv.gameObject.GetComponent<HunterWeaponController>() ?? myAv.gameObject.AddComponent<HunterWeaponController>();
                    _gun.Manager = this;
                }
            }

            _deadActors.Clear();
            _ending = false;

            _roundArmed = false;
            _roundEndRealtime = 0;
        }

        public void RequestEliminate(int targetActorNumber)
        {
            if (!PhotonNetwork.InRoom) return;
            var content = new object[] { EVT_TAG, PhotonNetwork.LocalPlayer.ActorNumber, targetActorNumber };
            PhotonNetwork.RaiseEvent(EVT_KILL, content, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        }

        public void OnEvent(EventData e)
        {
            try
            {
                if (HandleHunterWeaponEvent(e)) return;

                if (e.Code == EVT_KILL)
                {
                    if (!(e.CustomData is object[] arr) || arr.Length < 3) return;
                    if (!(arr[0] is string tag) || tag != EVT_TAG) return;
                    if (!TryToInt(arr[1], out int shooterActor) || shooterActor != _hunterActor) return;
                    if (!TryToInt(arr[2], out int targetActor) || targetActor <= 0) return;

                    if (PhotonNetwork.LocalPlayer.ActorNumber == targetActor)
                        KillLocalPlayerClean();
                }
                else if (e.Code == EVT_PLAYERDOWN)
                {
                    if (!(e.CustomData is object[] arr) || arr.Length < 2) return;
                    if (!(arr[0] is string tag) || tag != EVT_TAG) return;
                    if (!TryToInt(arr[1], out int deadActor)) return;

                    if (PhotonNetwork.IsMasterClient)
                    {
                        _deadActors.Add(deadActor);

                        if (deadActor == _hunterActor)
                        {
                            _roundArmed = false;
                            BroadcastMatchEnd(PHWinner.Hiders, "Hunter eliminated");
                            return;
                        }

                        if (AllHidersDead())
                        {
                            _roundArmed = false;
                            BroadcastMatchEnd(PHWinner.Hunter, "All hiders eliminated");
                        }
                    }
                }
                else if (e.Code == EVT_MATCHEND)
                {
                    if (!(e.CustomData is object[] arr) || arr.Length < 3) return;
                    if (!(arr[0] is string tag) || tag != EVT_TAG) return;
                    if (!TryToInt(arr[1], out int w)) return;
                    string reason = arr[2] as string ?? "";
                    StartEndBannerLocal((PHWinner)w, reason);
                }
            }
            catch (Exception ex) { LogGate.WarnThrottled(PropHuntPlugin.Log, $"[Empress Prop Hunt] OnEvent guarded: {ex.Message}"); }
        }

        bool AllHidersDead()
        {
            foreach (var p in PhotonNetwork.PlayerList)
            {
                if (p.IsInactive) continue;
                if (p.ActorNumber == _hunterActor) continue;
                if (!_deadActors.Contains(p.ActorNumber)) return false;
            }
            return true;
        }

        void KillLocalPlayerClean()
        {
            try
            {
                var me = FindLocalAvatar();
                if (me != null)
                {
                    me.PlayerDeath(0);
                    return;
                }
            }
            catch { }

            try
            {
                var me = FindLocalAvatar();
                var health = GameAccess.Health(me);
                if (me != null && health != null)
                    health.HurtOther(9999, me.transform.position, false, -1);
            }
            catch (Exception ex) { LogGate.WarnThrottled(PropHuntPlugin.Log, $"[Empress Prop Hunt] KillLocalPlayerClean: {ex.Message}"); }
        }

        void EnsureControllersOnAllAvatars()
        {
            var list = GameAccess.PlayerList();
            if (list == null) return;
            foreach (var p in list)
                if (p != null && p.GetComponent<PropDisguiseController>() == null)
                    p.gameObject.AddComponent<PropDisguiseController>();
        }

        void SyncAllPlayerDisguises()
        {
            foreach (var pl in PhotonNetwork.PlayerList)
            {
                var av = FindAvatarByActor(pl.ActorNumber);
                if (av == null) continue;
                ApplyOnePlayerDisguise(pl, av);
            }
        }

        void ApplyOnePlayerDisguise(Player pl, PlayerAvatar av)
        {
            if (pl == null || av == null) return;
            if (!pl.CustomProperties.ContainsKey(PLAYER_KEY_PROP)) return;
            if (!TryToInt(pl.CustomProperties[PLAYER_KEY_PROP], out int vid)) return;

            var ctrl = av.GetComponent<PropDisguiseController>() ?? av.gameObject.AddComponent<PropDisguiseController>();
            if (vid == 0 || PhotonView.Find(vid) == null)
            {
                ctrl.ApplyClearLocal();
                return;
            }

            ctrl.ApplyDisguiseLocal(vid, TINY_FACTOR);
            bool locked = pl.CustomProperties.ContainsKey(PLAYER_KEY_LOCKED) &&
                          TryToInt(pl.CustomProperties[PLAYER_KEY_LOCKED], out int lockValue) &&
                          lockValue == 1;
            ctrl.SetVisualPoseLocked(locked);
        }

        PlayerAvatar FindLocalAvatar()
        {
            var list = GameAccess.PlayerList();
            if (list == null) return null;
            foreach (var p in list)
                if (p != null && p.photonView != null && p.photonView.IsMine) return p;
            return null;
        }

        PlayerAvatar FindAvatarByActor(int actorNr)
        {
            var list = GameAccess.PlayerList();
            if (list == null) return null;
            foreach (var p in list)
            {
                if (p == null || p.photonView == null) continue;
                if (p.photonView.Owner != null && p.photonView.Owner.ActorNumber == actorNr) return p;
            }
            return null;
        }

        private ValuableObject GetTargetValuable()
        {
            var obj = PhysGrabber.instance?.currentlyLookingAtPhysGrabObject;
            if (obj == null) return null;
            return obj.GetComponent<ValuableObject>();
        }

        private void UpdateCachedTargetFromPhysGrab()
        {
            try
            {
                var v = GetTargetValuable();
                if (v == null) return;

                var pv = v.GetComponent<PhotonView>();
                if (pv != null && pv.ViewID != 0)
                    _cachedPropPhotonViewId = pv.ViewID;
            }
            catch { }
        }

        void ToggleDisguise(PlayerAvatar me)
        {
            var ctrl = me.GetComponent<PropDisguiseController>() ?? me.gameObject.AddComponent<PropDisguiseController>();

            if (ctrl.IsDisguised)
            {
                PhotonNetwork.LocalPlayer.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { PLAYER_KEY_PROP, 0 }, { PLAYER_KEY_LOCKED, 0 } });
                me.photonView.RPC(nameof(PropDisguiseController.PH_RPC_TinyOff), RpcTarget.OthersBuffered, me.photonView.ViewID);
                ctrl.ApplyClearLocal();
                _positionLocked = false;
                Flash("Disguise cleared.");
                return;
            }

            if (_cachedPropPhotonViewId == 0 || PhotonView.Find(_cachedPropPhotonViewId) == null)
            {
                Flash("No disguise target. Look at a Valuable.");
                return;
            }

            int propVid = _cachedPropPhotonViewId;

            PhotonNetwork.LocalPlayer.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { PLAYER_KEY_PROP, propVid }, { PLAYER_KEY_LOCKED, 0 } });
            me.photonView.RPC(nameof(PropDisguiseController.PH_RPC_TinyOn), RpcTarget.OthersBuffered, me.photonView.ViewID, propVid, TINY_FACTOR);
            ctrl.ApplyDisguiseLocal(propVid, TINY_FACTOR);
            _positionLocked = false;
            Flash("Disguised.");
        }

        static bool TryToInt(object o, out int val)
        {
            try
            {
                switch (o)
                {
                    case int i: val = i; return true;
                    case byte b: val = b; return true;
                    case short s: val = s; return true;
                    case long l: val = (int)l; return true;
                    case float f: val = (int)f; return true;
                    case double d: val = (int)d; return true;
                    case string g: if (int.TryParse(g, out var j)) { val = j; return true; } break;
                }
            }
            catch { }
            val = -1; return false;
        }
    }
    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.PlayerDeath))]
    public static class PH_Patch_PlayerDeath
    {
        static void Postfix(PlayerAvatar __instance)
        {
            try
            {
                if (!PhotonNetwork.InRoom) return;
                var owner = __instance?.photonView?.Owner;
                if (owner == null) return;

                const byte EVT_PLAYERDOWN = 102;
                var content = new object[] { "PH1", owner.ActorNumber };
                PhotonNetwork.RaiseEvent(EVT_PLAYERDOWN, content, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
            }
            catch { }
        }
    }
    [HarmonyPatch(typeof(PlayerAvatar), "OnPhotonSerializeView")]
    public static class PH_SpoofCrouch_OnSerialize
    {
        static void Prefix(PlayerAvatar __instance, PhotonStream stream, PhotonMessageInfo info, ref object __state)
        {
            __state = null;
            try
            {
                if (!stream.IsWriting) return;

                var ctrl = __instance.GetComponent<PropDisguiseController>();
                if (ctrl == null || !ctrl.IsDisguised) return;

                bool prior = GameAccess.GetIsCrouching(__instance);
                __state = prior;
                GameAccess.SetIsCrouching(__instance, true);
            }
            catch { }
        }

        static void Postfix(PlayerAvatar __instance, PhotonStream stream, PhotonMessageInfo info, object __state)
        {
            try
            {
                if (!stream.IsWriting) return;
                if (!(__state is bool prior)) return;

                GameAccess.SetIsCrouching(__instance, prior);
            }
            catch { }
        }
    }
    public class PropDisguiseController : MonoBehaviourPun
    {
        private PlayerAvatar _avatar;
        private PlayerAvatarVisuals _visuals;
        private GameObject _disguiseRoot;
        private Transform _tinyTarget;
        private Vector3 _tinyOrigScale;
        private bool _hasTinyOrig;
        private GameObject _flashlightGO;
        private bool _visualPoseLocked;
        private Vector3 _visualLockWorldPosition;
        private Quaternion _visualLockWorldRotation;

        public bool IsDisguised { get; private set; }

        public bool TryRaycastDisguise(Ray ray, float maxDistance, out float distance)
        {
            distance = 0f;
            if (!IsDisguised || _disguiseRoot == null) return false;

            float best = maxDistance;
            bool found = false;
            var renderers = _disguiseRoot.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!renderer.bounds.IntersectRay(ray, out float hitDistance)) continue;
                if (hitDistance < 0f || hitDistance > best) continue;
                best = hitDistance;
                found = true;
            }

            if (!found) return false;
            distance = best;
            return true;
        }
        private bool _thirdEnabled;
        private bool _camBaseValid;
        private Transform _camOrigParent;
        private Vector3 _camOrigLocalPos;
        private float _camBack, _camUp;
        private Vector3 _camVel;
        private float _camSmoothTime = 0.08f;

        static readonly string[] VISUALS_NAMES = { "Player Visuals", "PlayerVisuals", "Player Visual", "Player_Visuals", "Visuals" };
        static readonly string[] RIG_NAMES = { "[RIG]", "RIG", "Rig", "Armature", "Root", "RootTransform" };
        static readonly string[] FLASH_NAMES = { "flashlight", "flash_light", "flash", "torch", "headlamp", "head_lamp", "lamp" };

        void Awake()
        {
            _avatar = GetComponent<PlayerAvatar>();
            CacheVisualRefs();
        }

        void CacheVisualRefs()
        {
            if (_visuals == null) _visuals = GetComponentInChildren<PlayerAvatarVisuals>(true);
            if (_flashlightGO == null)
            {
                var t = FindFlashlightTransform(transform);
                if (t != null) _flashlightGO = t.gameObject;
            }
        }

        public void ApplyDisguiseLocal(int propVid, float tinyFactor) => DoApply(propVid, tinyFactor);
        public void ApplyClearLocal() => DoClear();

        [PunRPC]
        public void PH_RPC_TinyOn(int playerViewID, int propPhotonViewId, float tinyFactor)
        {
            if (photonView == null || photonView.ViewID != playerViewID || photonView.IsMine) return;
            DoApply(propPhotonViewId, tinyFactor);
        }

        [PunRPC]
        public void PH_RPC_TinyOff(int playerViewID)
        {
            if (photonView == null || photonView.ViewID != playerViewID || photonView.IsMine) return;
            DoClear();
        }

        void DoApply(int propPhotonViewId, float tinyFactor)
        {
            CacheVisualRefs();

            float f = Mathf.Clamp(tinyFactor, 0.0001f, 0.1f);

            if (!BuildOrRefreshClone(propPhotonViewId))
            {
                DoClear();
                return;
            }

            ApplyTinyToVisualRig(f);
            var grabber = GameAccess.AvatarGrabber(_avatar);
            if (grabber != null) grabber.enabled = false;

            if (photonView != null && photonView.IsMine) SetupLocalViewOffsets();

            IsDisguised = true;
        }

        void DoClear()
        {
            CacheVisualRefs();

            SetVisualPoseLocked(false);
            ClearTinyOnVisualRig();

            var grabber = GameAccess.AvatarGrabber(_avatar);
            if (grabber != null) grabber.enabled = true;

            IsDisguised = false;

            if (photonView != null && photonView.IsMine)
            {
                _thirdEnabled = false;
                RestoreLocalViewOffsets();
                _camOrigParent = null;
                _camOrigLocalPos = Vector3.zero;
            }

            RemoveClone();
        }
        void ApplyTinyToVisualRig(float factor)
        {
            _tinyTarget = ResolveTinyTarget(((Component)_avatar).transform);
            if (_tinyTarget == null) return;

            if (!_hasTinyOrig)
            {
                _tinyOrigScale = _tinyTarget.localScale;
                _hasTinyOrig = true;
            }

            var tiny = new Vector3(factor, factor, factor);
            if ((_tinyTarget.localScale - tiny).sqrMagnitude > 1e-8f)
                _tinyTarget.localScale = tiny;

            foreach (var l in GetComponentsInChildren<Light>(true))
                l.enabled = false;
        }

        void ClearTinyOnVisualRig()
        {
            if (_tinyTarget != null && _hasTinyOrig)
                _tinyTarget.localScale = _tinyOrigScale;

            foreach (var l in GetComponentsInChildren<Light>(true))
                l.enabled = true;
        }

        Transform FindFlashlightTransform(Transform root)
        {
            foreach (var n in FLASH_NAMES)
            {
                var t = root.Find(n) ?? root.Find(ToTitle(n));
                if (t != null) return t;
            }

            var q = new Queue<Transform>(); q.Enqueue(root);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                string cn = Normalize(cur.name);
                if (FLASH_NAMES.Any(n => cn.Contains(Normalize(n))))
                    return cur;

                foreach (Transform ch in cur) q.Enqueue(ch);
            }

            foreach (var l in root.GetComponentsInChildren<Light>(true))
            {
                var t = l.transform;
                while (t != null && t != root)
                {
                    if (FLASH_NAMES.Any(n => Normalize(t.name).Contains(Normalize(n))))
                        return t;
                    t = t.parent;
                }
            }
            return null;
        }

        static string ToTitle(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        Transform ResolveTinyTarget(Transform avatarRoot)
        {
            if (avatarRoot == null) return null;

            var usedRoot = FindNearestAncestorWhoseSubtreeHasLoose(avatarRoot, VISUALS_NAMES) ?? avatarRoot;

            var visuals = FindLooseBFS(usedRoot, VISUALS_NAMES);
            var rig = (visuals != null) ? FindLooseBFS(visuals, RIG_NAMES) : null;

            if (rig == null) rig = FindLooseBFS(avatarRoot, RIG_NAMES);
            if (rig != null) return rig;
            if (visuals != null) return visuals;

            var r = avatarRoot.GetComponentInChildren<Renderer>(true);
            return r != null ? r.transform : avatarRoot;
        }

        static Transform FindLooseBFS(Transform root, params string[] names)
        {
            if (root == null) return null;
            string[] targets = names.Select(Normalize).ToArray();

            foreach (Transform c in root)
            {
                string cn = Normalize(c.name);
                if (targets.Any(t => t == cn)) return c;
            }

            var q = new Queue<Transform>();
            q.Enqueue(root);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (Transform ch in cur)
                {
                    string cn = Normalize(ch.name);
                    if (targets.Any(t => t == cn || cn.Contains(t))) return ch;
                    q.Enqueue(ch);
                }
            }
            return null;
        }

        static Transform FindNearestAncestorWhoseSubtreeHasLoose(Transform start, params string[] names)
        {
            var t = start;
            while (t != null)
            {
                if (FindLooseBFS(t, names) != null) return t;
                t = t.parent;
            }
            return null;
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            var filtered = s.Where(ch => ch != ' ' && ch != '_' && ch != '-' && ch != '[' && ch != ']' && ch != '(' && ch != ')' && ch != '{' && ch != '}' && ch != '.');
            return new string(filtered.ToArray());
        }
        bool BuildOrRefreshClone(int propPhotonViewId)
        {
            RemoveClone();

            var valView = PhotonView.Find(propPhotonViewId);
            if (valView == null)
            {
                PropHuntPlugin.Log.LogWarning($"[Empress Prop Hunt] Valuable view {propPhotonViewId} not found.");
                return false;
            }

            _disguiseRoot = new GameObject("PH_Disguise");
            _disguiseRoot.transform.SetParent(_avatar.transform, false);
            _disguiseRoot.transform.localPosition = Vector3.zero;
            _disguiseRoot.transform.localRotation = Quaternion.identity;
            _disguiseRoot.transform.localScale = Vector3.one;

            int layer = 0;
            CloneRelativeModel(valView.transform, _disguiseRoot.transform, layer);

            foreach (var c in _disguiseRoot.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            foreach (var rb in _disguiseRoot.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.Destroy(rb);
            return true;
        }

        void RemoveClone()
        {
            if (_disguiseRoot != null) { UnityEngine.Object.Destroy(_disguiseRoot); _disguiseRoot = null; }
        }

        public void SetVisualPoseLocked(bool locked)
        {
            if (!locked)
            {
                _visualPoseLocked = false;
                if (_disguiseRoot != null)
                {
                    _disguiseRoot.transform.localPosition = Vector3.zero;
                    _disguiseRoot.transform.localRotation = Quaternion.identity;
                }
                return;
            }

            if (!IsDisguised || _disguiseRoot == null) return;
            _visualLockWorldPosition = _disguiseRoot.transform.position;
            _visualLockWorldRotation = _disguiseRoot.transform.rotation;
            _visualPoseLocked = true;
            MaintainVisualLockPose();
        }

        void MaintainVisualLockPose()
        {
            if (!_visualPoseLocked || _disguiseRoot == null) return;

            var root = _disguiseRoot.transform;
            var parent = root.parent;
            if (parent == null)
            {
                root.position = _visualLockWorldPosition;
                root.rotation = _visualLockWorldRotation;
                return;
            }

            root.localPosition = parent.InverseTransformPoint(_visualLockWorldPosition);
            root.localRotation = Quaternion.Inverse(parent.rotation) * _visualLockWorldRotation;
        }

        void CloneRelativeModel(Transform srcRoot, Transform dstRoot, int avatarLayer) => CloneNodeRecursive(srcRoot, dstRoot, avatarLayer, true);

        void CloneNodeRecursive(Transform src, Transform dstParent, int avatarLayer, bool isRoot)
        {
            var go = new GameObject(src.name + "_PH");
            go.layer = avatarLayer;
            go.transform.SetParent(dstParent, false);

            if (isRoot)
            {
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = src.localScale;
            }
            else
            {
                go.transform.localPosition = src.localPosition;
                go.transform.localRotation = src.localRotation;
                go.transform.localScale = src.localScale;
            }

            var mf = src.GetComponent<MeshFilter>();
            var mr = src.GetComponent<MeshRenderer>();
            var smr = src.GetComponent<SkinnedMeshRenderer>();

            if (mr != null && mf != null && mf.sharedMesh != null)
            {
                var newMf = go.AddComponent<MeshFilter>();
                var newMr = go.AddComponent<MeshRenderer>();
                newMf.sharedMesh = mf.sharedMesh;
                newMr.sharedMaterials = mr.sharedMaterials;
                newMr.enabled = true; newMr.forceRenderingOff = false;
            }
            else if (smr != null && smr.sharedMesh != null)
            {
                var baked = new Mesh();
                try { smr.BakeMesh(baked); } catch { baked = UnityEngine.Object.Instantiate(smr.sharedMesh); }
                var newMf = go.AddComponent<MeshFilter>();
                var newMr = go.AddComponent<MeshRenderer>();
                newMf.sharedMesh = baked;
                newMr.sharedMaterials = smr.sharedMaterials;
                newMr.enabled = true; newMr.forceRenderingOff = false;
            }

            for (int i = 0; i < src.childCount; i++)
                CloneNodeRecursive(src.GetChild(i), go.transform, avatarLayer, false);
        }
        void SetupLocalViewOffsets()
        {
            _thirdEnabled = true;
            var cam = Camera.main; if (cam == null) return;
            if (_camOrigParent == null && !_camBaseValid)
            {
                _camOrigParent = cam.transform.parent;
                _camOrigLocalPos = cam.transform.localPosition;
            }
            if (_camOrigParent == null)
            {
                _camOrigParent = cam.transform.parent;
                _camOrigLocalPos = cam.transform.localPosition;
            }

            float r = ComputeBoundsRadius(_disguiseRoot != null ? _disguiseRoot.transform : transform);
            _camBack = Mathf.Max(1.6f, r * 2.2f);
            _camUp = Mathf.Clamp(r * 0.6f, 0.25f, 1.2f);
        }

        void RestoreLocalViewOffsets()
        {
            var cam = Camera.main;
            if (cam == null) { _camBaseValid = false; return; }

            _thirdEnabled = false;
            _camBaseValid = false;
            _camVel = Vector3.zero;

            Vector3 baseWorld = (_camOrigParent != null)
                ? _camOrigParent.TransformPoint(_camOrigLocalPos)
                : cam.transform.position;

            if (cam.transform.parent == _camOrigParent)
                cam.transform.localPosition = _camOrigLocalPos;
            else if (cam.transform.parent != null)
                cam.transform.localPosition = cam.transform.parent.InverseTransformPoint(baseWorld);
            else
                cam.transform.position = baseWorld;
        }

        float ComputeBoundsRadius(Transform root)
        {
            var rends = root.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return 0.6f;
            var b = new Bounds(rends[0].bounds.center, Vector3.zero);
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b.extents.magnitude * 0.75f;
        }

        void LateUpdate()
        {
            if (IsDisguised) MaintainVisualLockPose();
            if (!IsDisguised || !_thirdEnabled) return;
            var cam = Camera.main; if (cam == null) return;

            Vector3 baseWorld = (_camOrigParent != null)
                ? _camOrigParent.TransformPoint(_camOrigLocalPos)
                : cam.transform.position;

            Vector3 offsetWorld = (_camOrigParent != null)
                ? _camOrigParent.TransformDirection(new Vector3(0f, _camUp, -_camBack))
                : cam.transform.rotation * new Vector3(0f, _camUp, -_camBack);

            Vector3 desiredWorld = baseWorld + offsetWorld;

            cam.transform.position = Vector3.SmoothDamp(cam.transform.position, desiredWorld, ref _camVel, _camSmoothTime);
        }

        void OnDisable()
        {
            if (photonView != null && photonView.IsMine)
            {
                _thirdEnabled = false;
                RestoreLocalViewOffsets();
            }
        }
        void OnDestroy()
        {
            if (photonView != null && photonView.IsMine)
            {
                _thirdEnabled = false;
                RestoreLocalViewOffsets();
            }
        }
        public bool ToggleDisguiseView()
        {
            if (!IsDisguised) return _thirdEnabled;
            _thirdEnabled = !_thirdEnabled;
            if (!_thirdEnabled) RestoreLocalViewOffsets();
            else SetupLocalViewOffsets();
            return _thirdEnabled;
        }
    }
}
