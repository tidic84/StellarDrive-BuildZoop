#if DEV
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using BuildZoop.Zoop;
using Managers.Interface;
using Managers.Interface.Model;
using Managers.Interface.Services;
using MelonLoader;
using MelonLoader.Utils;
using Players.LocalClient.Controls;
using SDModKit.Game;
using Ships;
using Ships.Interface.Model;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using Ships.Services;
using UnityEngine;
using UnityEngine.SceneManagement;
using WorldTracking.Interface.Model;
using WorldTracking.Interface.Values;

namespace BuildZoop.Dev
{
    /// <summary>Dev-only remote control: loads UserData-free test world BZ_Test and runs commands from UserData/buildzoop_cmd.txt.</summary>
    public static class DevHarness
    {
        private const string TestWorld = "BZ_Test";
        private static string _cmdFile;
        private static float _nextPoll;
        private static bool _launched;
        private static MelonLogger.Instance _log;

        public static void Init(MelonLogger.Instance log)
        {
            _log = log;
            var place = HarmonyLib.AccessTools.GetDeclaredMethods(typeof(Managers.Server.Services.ShipInteractionService))
                .First(m => m.Name.StartsWith("RpcLogic___PlaceHullPart_ServerRPC"));
            new HarmonyLib.Harmony("buildzoop.dev").Patch(place, new HarmonyLib.HarmonyMethod(typeof(DevHarness), nameof(TracePlace)));
            _cmdFile = Path.Combine(MelonEnvironment.UserDataDirectory, "buildzoop_cmd.txt");
            if (File.Exists(_cmdFile)) File.Delete(_cmdFile);
        }

        public static void SceneLoaded(int buildIndex, string sceneName)
        {
            _log.Msg($"[DEV] scene loaded {buildIndex} {sceneName}");
            if (buildIndex == 0 && !_launched)
            {
                _launched = true;
                MelonCoroutines.Start(Launch());
            }
        }

        private static IEnumerator Launch()
        {
            for (int i = 0; i < 30; i++) yield return null;
            string file = Path.Combine(Application.persistentDataPath, "WorldSaves", TestWorld, "world.json");
            if (!File.Exists(file))
            {
                _log.Error($"[DEV] test world missing: {file}");
                yield break;
            }
            MainGameSceneLoadData.Mode = MainGameSceneLoadData.GameMode.SinglePlayer;
            MainGameSceneLoadData.SaveFileFullName = new FileInfo(file).FullName;
            _log.Msg($"[DEV] launching {TestWorld}");
            SceneManager.LoadScene("MainGame", LoadSceneMode.Single);
        }

        public static void Update()
        {
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + 0.5f;
            if (!File.Exists(_cmdFile)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(_cmdFile);
                File.Delete(_cmdFile);
            }
            catch (IOException)
            {
                return;
            }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                _log.Msg($"[DEV] cmd: {line}");
                try
                {
                    Run(line.Split(' '));
                }
                catch (Exception ex)
                {
                    _log.Error($"[DEV] cmd failed: {ex}");
                }
            }
        }

        private static void TracePlace(object __instance, object[] __args)
        {
            var P_0 = (PlaceHullPartNetworkRequest)__args[0];
            var P_1 = __args[1] as FishNet.Connection.NetworkConnection;
            var players = HarmonyLib.Traverse.Create(__instance).Field("_players").GetValue<Players.Services.PlayersServerTracker>();
            var player = P_1 == null || players == null ? null : players.GetTrackedPlayerFromConnectionId(P_1.ClientId);
            _log.Msg($"[DEV] server place ship={P_0.ShipId} settings={P_0.SettingsId} at={P_0.Placement.Coord} conn={(P_1 == null ? "null" : P_1.ClientId.ToString())} player={(player == null ? "none" : player.Name + " free=" + player.IsUsingCheatFreeBuild + " items=" + player.HasItemsInInventory(Resources.FindObjectsOfTypeAll<PartSettings>().First(s => s.id == P_0.SettingsId).BuildingCost))}");
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private static ShipsClientTracker Ships => GameRefs.Service<ShipsClientTracker>();

        private static void Run(string[] a)
        {
            switch (a[0])
            {
                case "parts":
                    foreach (var s in Resources.FindObjectsOfTypeAll<HullPartSettings>().OrderBy(s => s.id))
                        _log.Msg($"[DEV] hull {s.id} '{s.fullLabel}' style={s.snappingStyle} size={s.size} pivoted={(s.pivotedSettings != null ? s.pivotedSettings.id.ToString() : "-")} fwd={s.defaultForward} up={s.defaultUp}");
                    break;
                case "ships":
                {
                    Camera cam = GameRefs.PlayerCamera;
                    foreach (var ship in Ships.Ships)
                    {
                        int hull = 0;
                        if (ship.IsRigidbodyLoaded)
                        {
                            float d = cam == null ? -1f : Vector3.Distance(cam.transform.position, ship.ShipRigidBody.transform.position);
                            _log.Msg($"[DEV] ship {ship.Id} visual={ship.IsVisualLoaded} dist={d:F1} parts={ship.StatefulParts.Count()} hull={hull}");
                        }
                    }
                    break;
                }
                case "newship":
                {
                    // newship <hullSettingsId> <distance>: a one-panel ship floating in front of the camera
                    Camera cam = GameRefs.PlayerCamera;
                    Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                    Vector3 pos = cam.transform.position + fwd * F(a[2]) + Vector3.up * 0.5f;
                    GameRefs.Service<IShipInteractionNetworkService>().CreateShip(new CreateShipNetworkRequest
                    {
                        PartId = ushort.Parse(a[1]),
                        WorldTransform = new WorldTransform { SpaceId = ClientOrigin.LocalPhysicsSpaceId, Position = pos, Rotation = Quaternion.LookRotation(fwd, Vector3.up) }
                    });
                    _log.Msg($"[DEV] create ship at {pos}");
                    break;
                }
                case "hull":
                {
                    // hull <shipId>: every hull panel of the ship
                    var ship = Aim.Ship(uint.Parse(a[1]));
                    int n = 0;
                    for (int x = -12; x <= 12; x++)
                        for (int y = -12; y <= 12; y++)
                            for (int z = -12; z <= 12; z++)
                            {
                                var voxel = ship.GetVoxel(new Vector3Int(x, y, z));
                                if (voxel == null) continue;
                                foreach (VoxelPartType t in new[] { VoxelPartType.Floor, VoxelPartType.ForwardWall, VoxelPartType.RightWall, VoxelPartType.Interior, Pairing.SharedVoxel.Second })
                                {
                                    var p = voxel.GetPartFromType(t);
                                    if (!p.HasValue || p.Value.Settings == null) continue;
                                    n++;
                                    _log.Msg($"[DEV] panel {p.Value.HullPlacement.Coord} {t} fwd={p.Value.ForwardOrientation} up={p.Value.UpOrientation} id={p.Value.Settings.id}");
                                }
                            }
                    _log.Msg($"[DEV] {n} panels");
                    break;
                }
                case "look":
                {
                    // look <shipId> x y z: point the camera at a ship-local point
                    var tracked = Ships.Ships.First(s => s.Id == uint.Parse(a[1]));
                    Vector3 target = tracked.ShipRigidBody.transform.TransformPoint(new Vector3(F(a[2]), F(a[3]), F(a[4])));
                    var look = UnityEngine.Object.FindObjectsByType<PlayerLookControls>(FindObjectsSortMode.None).First();
                    Camera cam = Camera.main;
                    look.Rotation = Quaternion.LookRotation(target - cam.transform.position, look.Rotation * Vector3.up);
                    break;
                }
                case "zoop":
                {
                    // zoop <shipId> <settingsId> <x> <y> <z> <type> <fwd> <up> <mode> <ax> <ay> <az> [dry]
                    uint shipId = uint.Parse(a[1]);
                    var settings = Resources.FindObjectsOfTypeAll<PartSettings>().First(s => s.id == ushort.Parse(a[2]));
                    var anchor = new HullPartPlacement(new Vector3Int(I(a[3]), I(a[4]), I(a[5])),
                        (VoxelPartType)Enum.Parse(typeof(VoxelPartType), a[6]),
                        (PartOrientation)Enum.Parse(typeof(PartOrientation), a[7]),
                        (PartOrientation)Enum.Parse(typeof(PartOrientation), a[8]));
                    Settings.CurrentMode = (ZoopMode)Enum.Parse(typeof(ZoopMode), a[9]);
                    ZoopBuilder.DevAim = new Vector3(F(a[10]), F(a[11]), F(a[12]));
                    ZoopBuilder.DevStart(shipId, settings, anchor);
                    ZoopBuilder.DevTrack();
                    _log.Msg("[DEV] " + ZoopBuilder.DevDescribe());
                    if (a.Length > 13 && a[13] == "dry")
                        ZoopBuilder.Cancel();
                    else
                        _log.Msg($"[DEV] commit {ZoopBuilder.TryCommit()} {ZoopBuilder.LastAction}");
                    ZoopBuilder.DevAim = null;
                    break;
                }
                case "freebuild":
                    PlayerAccess.LocalNetworkState.ServerSetUsingCheatFreeBuild(a.Length < 2 || a[1] != "off");
                    _log.Msg($"[DEV] free build {PlayerAccess.LocalNetworkState.IsUsingCheatFreeBuild}");
                    break;
                case "snap":
                {
                    // snap <shipId> <settingsId> <forward> <ax> <ay> <az> [place]: the game's own snapping, camera aimed at a ship-local point
                    uint shipId = uint.Parse(a[1]);
                    var settings = Resources.FindObjectsOfTypeAll<PartSettings>().First(x => x.id == ushort.Parse(a[2]));
                    var fwd = (PartOrientation)Enum.Parse(typeof(PartOrientation), a[3]);
                    var tracked = Ships.Ships.First(x => x.Id == shipId);
                    Transform rb = tracked.ShipRigidBody.transform;
                    Vector3 target = rb.TransformPoint(new Vector3(F(a[4]), F(a[5]), F(a[6])));
                    Camera cam = GameRefs.PlayerCamera;
                    var detection = Core.Services.ServiceLocator.GetService<Tools.Build.PartPreviewDetection>();
                    var result = detection.CalculateSnapDetectionUpdate(new Tools.Build.Model.SnapDetectionState
                    {
                        partSettings = settings,
                        partRotation = rb.rotation * Quaternion.LookRotation(fwd.GetVector3(), Mathf.Abs(fwd.GetVector3().normalized.y) > 0.9f ? Vector3.forward : Vector3.up)
                    }, new Core.Utils.CameraPhysicsData { CameraPos = cam.transform.position, Direction = (target - cam.transform.position).normalized });
                    _log.Msg($"[DEV] snap snapped={result.IsSnapped} placement={result.placement} cam={rb.InverseTransformPoint(cam.transform.position)}");
                    if (a.Length > 7 && a[7] == "place" && result.IsSnapped && result.placement is HullPartPlacement hp)
                    {
                        ushort id = settings.id;
                        if (settings is HullPartSettings hs && hs.pivotedSettings != null && (hp.ForwardOrientation == PartOrientation.ForwardRight || hp.ForwardOrientation == PartOrientation.ForwardLeft))
                            id = hs.pivotedSettings.id;
                        GameRefs.Service<IShipInteractionNetworkService>().PlaceHullPart(new PlaceHullPartNetworkRequest { ShipId = shipId, SettingsId = id, Placement = hp });
                        _log.Msg($"[DEV] placed {id} at {hp.Position}");
                    }
                    break;
                }
                case "hover":
                {
                    // hover <shipId> <ax> <ay> <az>: what the recycler would target looking at that ship-local point
                    var tracked = Ships.Ships.First(x => x.Id == uint.Parse(a[1]));
                    Vector3 target = tracked.ShipRigidBody.transform.TransformPoint(new Vector3(F(a[2]), F(a[3]), F(a[4])));
                    Camera cam = GameRefs.PlayerCamera;
                    Vector3 dir = (target - cam.transform.position).normalized;
                    if (!ClientScene.PhysicsScene.Raycast(cam.transform.position, dir, out RaycastHit hit, 10f, Tools.Common.RaycastMasks.BuildDetectionLayer))
                    {
                        _log.Msg("[DEV] hover: nothing");
                        break;
                    }
                    var hover = new Tools.Common.Hover.HullHover(hit, dir);
                    _log.Msg($"[DEV] hover tag={hit.collider.tag} position={HarmonyLib.Traverse.Create(hover).Field("_hullPartPosition").GetValue()}");
                    break;
                }
                case "serverhull":
                {
                    var server = GameRefs.Service<ShipsServerTracker>().GetShip(uint.Parse(a[1]));
                    var parts = HarmonyLib.Traverse.Create(server).Property("State").GetValue<Ships.Interface.Model.ShipState>().GetHullParts();
                    _log.Msg($"[DEV] server hull {parts.Count}: " + string.Join(" ", parts.Keys.Select(k => k.VoxelCoord + ":" + (int)k.Type)));
                    var client = Aim.Ship(uint.Parse(a[1]));
                    _log.Msg($"[DEV] client state {HarmonyLib.Traverse.Create(client).Property("State").GetValue<Ships.Interface.Model.ShipState>()?.GetHullParts().Count}");
                    break;
                }
                case "equip":
                    // equip <partId>: build tool out with that part selected
                    LocalPlayer.TryEquip(LocalPlayer.BuildToolItem);
                    GameRefs.Service<Players.Interface.Services.ILocalSelectedPartProvider>().SelectPart(ushort.Parse(a[1]));
                    _log.Msg($"[DEV] build active={ToolRefs.BuildActive}");
                    break;
                case "key":
                {
                    // key <Key> down|up: fakes the keyboard state (only that key held)
                    UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
                    var kb = UnityEngine.InputSystem.Keyboard.current;
                    var keyState = a[2] == "down"
                        ? new UnityEngine.InputSystem.LowLevel.KeyboardState((UnityEngine.InputSystem.Key)Enum.Parse(typeof(UnityEngine.InputSystem.Key), a[1]))
                        : new UnityEngine.InputSystem.LowLevel.KeyboardState();
                    UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb, keyState);
                    break;
                }
                case "givecost":
                {
                    // givecost <partId> <times>: the materials to build that part that many times
                    var settings = Resources.FindObjectsOfTypeAll<PartSettings>().First(x => x.id == ushort.Parse(a[1]));
                    foreach (var item in settings.BuildingCost)
                    {
                        PlayerAccess.TryGive(item.ItemId, item.Quantity * int.Parse(a[2]), out string error);
                        _log.Msg($"[DEV] give {item.ItemId} x{item.Quantity * int.Parse(a[2])} {error}");
                    }
                    break;
                }
                case "hold":
                    ZoopBuilder.DevHold = a[1] == "on";
                    break;
                case "click":
                    ToolRefs.Build.PrimaryActionBegin();
                    ToolRefs.Build.PrimaryActionEnd();
                    break;
                case "state":
                {
                    var obj = Aim.PreviewObject(ToolRefs.Build);
                    int on = obj == null ? -1 : obj.GetComponentsInChildren<Renderer>().Count(r => r.enabled);
                    _log.Msg($"[DEV] zoop active={ZoopBuilder.Active} count={ZoopBuilder.Count} shift={UnityEngine.InputSystem.Keyboard.current.leftShiftKey.isPressed} previewRenderersOn={on} status={ZoopBuilder.Status}");
                    break;
                }
                case "save":
                    UnityEngine.Object.FindFirstObjectByType<Saving.Services.GameSaver>().SaveGame(new Saving.Interface.Model.SaveGameParams { IsAutomatic = false });
                    _log.Msg("[DEV] saving");
                    break;
                case "undo":
                    ZoopBuilder.Undo();
                    _log.Msg($"[DEV] {ZoopBuilder.LastAction}");
                    break;
                case "shot":
                {
                    string path = Path.Combine(MelonEnvironment.UserDataDirectory, "bz_" + (a.Length > 1 ? a[1] : "x") + ".png");
                    ScreenCapture.CaptureScreenshot(path);
                    _log.Msg($"[DEV] screenshot {path}");
                    break;
                }
                case "quit":
                    Application.Quit();
                    break;
            }
        }
    }
}
#endif
