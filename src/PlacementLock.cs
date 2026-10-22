using System.Reflection;
using Core.Utils;
using HarmonyLib;
using Players.Interface.Services;
using SDModKit.Game;
using SDModKit.Input;
using SDModKit.Util;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Tools.Build;
using Tools.Build.Model;
using UnityEngine;

namespace BuildZoop
{
    /// <summary>
    /// Freezes the build preview where it is, then moves it one cell at a time with the nudge keys
    /// (relative to where the camera looks).
    /// </summary>
    public static class PlacementLock
    {
        private const float FineStep = 0.25f;

        private static readonly FieldInfo PreviewField = AccessTools.Field(typeof(BuildToolLocalClient), "_partPreview");
        private static readonly FieldInfo SnappedField = AccessTools.Field(typeof(PartPreview), "_snappedPlacement");
        private static readonly FieldInfo PivotField = AccessTools.Field(typeof(PartPreview), "_detectedPivot");
        private static readonly FieldInfo ShipField = AccessTools.Field(typeof(PartPreview), "_ship");
        private static readonly FieldInfo TargetField = AccessTools.Field(typeof(PartPreview), "_previewTargetPos");
        private static readonly MethodInfo UpdateSnapped = AccessTools.Method(typeof(PartPreview), "UpdateSnappedPlacement");
        private static readonly MethodInfo PlaceSnapped = AccessTools.Method(typeof(PartPreview), "PlaceSnappedPreview");
        private static readonly MethodInfo HasMaterial = AccessTools.Method(typeof(PartPreview), "CheckHasEnoughMaterial");
        private static readonly MethodInfo MarkValid = AccessTools.Method(typeof(PartPreview), "MarkPreviewAsValid");
        private static readonly MethodInfo MarkInvalid = AccessTools.Method(typeof(PartPreview), "MarkPreviewAsInvalid");

        private static PartPreview _locked;
        private static bool _available;

        public static Keybind LockKey;
        public static Keybind Forward;
        public static Keybind Back;
        public static Keybind Left;
        public static Keybind Right;
        public static Keybind Up;
        public static Keybind Down;

        public static bool Locked => _locked != null;

        public static void Apply(HarmonyLib.Harmony harmony, Log log)
        {
            // Rotate also carries the rotation lock, so it is patched even when the lock internals are missing.
            Patch(harmony, "Rotate", nameof(BeforeRotate));
            if (SnappedField == null || ShipField == null || TargetField == null || UpdateSnapped == null || PlaceSnapped == null)
            {
                log.Warn("part preview internals not found, placement lock disabled");
                return;
            }
            Patch(harmony, "UpdatePlacement", nameof(BeforeUpdatePlacement));
            Patch(harmony, "ResetSnap", nameof(BeforeResetSnap));
            Patch(harmony, "OnPartSelected", nameof(BeforePartChange));
            Patch(harmony, "OnPartUnselected", nameof(BeforePartChange));
            _available = true;
        }

        private static void Patch(HarmonyLib.Harmony harmony, string method, string prefix)
        {
            var target = AccessTools.Method(typeof(PartPreview), method);
            if (target != null)
                harmony.Patch(target, new HarmonyMethod(typeof(PlacementLock), prefix));
        }

        public static void Update(BuildToolLocalClient build)
        {
            if (!_available || build == null || !build.isActiveAndEnabled || PreviewField == null)
            {
                Unlock();
                return;
            }
            var preview = PreviewField.GetValue(build) as PartPreview;
            if (LockKey != null && LockKey.Pressed && !LocalPlayer.GameMenuOpen)
            {
                if (Locked)
                    Unlock();
                else if (preview != null && Snapped(preview) != null && Ship(preview) != null)
                    _locked = preview;
            }
            if (!Locked)
                return;
            if (_locked != preview || Snapped(_locked) == null || Ship(_locked) == null)
            {
                Unlock();
                return;
            }
            Vector3Int move = ReadMove(Ship(_locked));
            if (move != Vector3Int.zero)
                Move(move);
        }

        public static void Unlock()
        {
            _locked = null;
        }

        private static void Move(Vector3Int move)
        {
            var snapped = Snapped(_locked);
            IPartPlacement next;
            if (snapped.HullPartPlacement.HasValue)
            {
                var hull = snapped.HullPartPlacement.Value;
                var position = hull.Position;
                position.VoxelCoord += move;
                hull.Position = position;
                next = hull;
            }
            else
            {
                var floating = snapped.FloatingPartPlacement.Value;
                floating.Coord += (Vector3)move * FineStep;
                next = floating;
            }
            UpdateSnapped.Invoke(_locked, new object[] { next });
            PlaceSnapped.Invoke(_locked, null);
        }

        /// <summary>Nudge keys to a ship axis: forward is the ship axis closest to where the camera looks.</summary>
        private static Vector3Int ReadMove(IShipPartPlacements ship)
        {
            int forward = Axis(Forward, Back), right = Axis(Right, Left), up = Axis(Up, Down);
            if (forward == 0 && right == 0 && up == 0)
                return Vector3Int.zero;
            Camera cam = GameRefs.PlayerCamera;
            if (cam == null)
                return Vector3Int.zero;
            Quaternion toShip = Quaternion.Inverse(ship.ToWorldRotation(Quaternion.identity));
            var upProvider = GameRefs.Service<IPlayerUpProvider>();
            Vector3 worldUp = upProvider != null ? upProvider.WorldPlayerUp : cam.transform.up;
            Vector3Int shipUp = Zoop.ZoopGeometry.DominantAxis(toShip * worldUp);
            Vector3 look = Vector3.ProjectOnPlane(toShip * cam.transform.forward, shipUp);
            if (look.sqrMagnitude < 0.01f)
                look = Vector3.ProjectOnPlane(toShip * cam.transform.up, shipUp);
            Vector3Int shipForward = Zoop.ZoopGeometry.DominantAxis(look);
            Vector3Int shipRight = Vector3Int.RoundToInt(Vector3.Cross(shipUp, shipForward));
            return shipForward * forward + shipRight * right + shipUp * up;
        }

        private static int Axis(Keybind plus, Keybind minus)
        {
            int n = 0;
            if (plus != null && plus.Pressed) n++;
            if (minus != null && minus.Pressed) n--;
            return n;
        }

        private static SnappedPreviewPlacement Snapped(PartPreview preview)
        {
            return preview == null ? null : SnappedField.GetValue(preview) as SnappedPreviewPlacement;
        }

        private static IShipPartPlacements Ship(PartPreview preview)
        {
            var ship = preview == null ? null : ShipField.GetValue(preview) as IShipPartPlacements;
            // A destroyed ship proxy is a Unity object that compares equal to null.
            if (ship is Object unityObject && unityObject == null)
                return null;
            return ship;
        }

        public static bool BeforeUpdatePlacement(PartPreview __instance, CameraPhysicsData cameraPhysics)
        {
            if (__instance != _locked)
                return true;
            var snapped = Snapped(__instance);
            var ship = Ship(__instance);
            if (snapped == null || ship == null)
            {
                Unlock();
                return true;
            }
            TargetField.SetValue(__instance, ship.ToWorldPos(snapped.PartPlacement) - cameraPhysics.CameraPos);
            PlaceSnapped.Invoke(__instance, null);
            bool enough = HasMaterial == null || (bool)HasMaterial.Invoke(__instance, null);
            (enough ? MarkValid : MarkInvalid)?.Invoke(__instance, null);
            return false;
        }

        public static bool BeforeResetSnap(PartPreview __instance)
        {
            return __instance != _locked;
        }

        public static bool BeforeRotate(PartPreview __instance, float direction)
        {
            if (Settings.RotationLocked.Value)
                return false;
            if (__instance != _locked)
                return true;
            if (PivotField != null && PivotField.GetValue(__instance) != null)
                return true;
            var snapped = Snapped(__instance);
            if (snapped == null || !snapped.FloatingPartPlacement.HasValue)
                return false;
            // Locked floating part: turn a quarter around its own up axis without losing the lock.
            var floating = snapped.FloatingPartPlacement.Value;
            Vector3 up = floating.UpOrientation.GetVector3();
            Vector3 forward = Quaternion.AngleAxis(direction > 0f ? 90f : -90f, up) * floating.ForwardOrientation.GetVector3();
            floating.ForwardOrientation = PartOrientationHelper.SnapAnyOrientation(forward);
            UpdateSnapped.Invoke(__instance, new object[] { floating });
            PlaceSnapped.Invoke(__instance, null);
            return false;
        }

        public static void BeforePartChange()
        {
            Unlock();
        }
    }
}
