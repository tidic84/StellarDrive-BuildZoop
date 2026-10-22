using System.Linq;
using System.Reflection;
using FishNet.Connection;
using HarmonyLib;
using Managers.Server.Services;
using Players.Interface.Services;
using SDModKit.Game;
using SDModKit.Util;
using Ships;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.State;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Tools.Build;
using Tools.Common.Hover;
using UnityEngine;

namespace BuildZoop
{
    /// <summary>
    /// Middle click a part to select it in the build tool, with its rotation and its settings
    /// (constant value, gate operation, channel, beacon name, lever height).
    /// </summary>
    public static class Pick
    {
        private static readonly FieldInfo HullPositionField = AccessTools.Field(typeof(HullHover), "_hullPartPosition");
        private static readonly FieldInfo HullShipField = AccessTools.Field(typeof(HullHover), "_currentShip");
        private static readonly FieldInfo ObjectPreviewField = AccessTools.Field(typeof(PartPreview), "_objectPreview");
        private static readonly FieldInfo PreviewSettingsField = AccessTools.Field(typeof(PartPreview), "_actualPreviewSettings");
        private static readonly FieldInfo PreviewShipField = AccessTools.Field(typeof(PartPreview), "_ship");

        private static IStatefulPartState _state;
        private static ushort _settingsId;
        private static Quaternion? _rotation;
        private static bool _placingLocal;

        public static string LastResult { get; private set; }
        public static string Copied { get; private set; }

        public static void Apply(HarmonyLib.Harmony harmony, Log log)
        {
            var place = AccessTools.GetDeclaredMethods(typeof(ShipInteractionService))
                .FirstOrDefault(m => m.Name.StartsWith("RpcLogic___PlaceFloatingPart_ServerRPC"));
            var add = AccessTools.Method(typeof(TrackedShipServer), "AddStatefulPart");
            if (place == null || add == null)
            {
                log.Warn("placement hooks not found, pick will not copy settings");
                return;
            }
            harmony.Patch(place, new HarmonyMethod(typeof(Pick), nameof(BeforeServerPlace)), new HarmonyMethod(typeof(Pick), nameof(AfterServerPlace)));
            harmony.Patch(add, postfix: new HarmonyMethod(typeof(Pick), nameof(AfterAddPart)));
        }

        public static void Click()
        {
            if (!LocalPlayer.IsOnFoot || LocalPlayer.GameMenuOpen)
                return;
            var selection = GameRefs.Service<ILocalSelectedPartProvider>();
            Camera cam = GameRefs.PlayerCamera;
            if (selection == null || cam == null)
            {
                LastResult = "sélection indisponible";
                return;
            }
            Vector3 origin = cam.transform.position, dir = cam.transform.forward;
            int count = GameRay.From(origin, dir, 12f, out RaycastHit[] hits);
            for (int i = 0; i < count; i++)
            {
                if (hits[i].collider == null)
                    continue;
                if (PickFloating(hits[i], selection) || PickHull(hits[i], dir, selection))
                    return;
            }
            LastResult = "rien à copier";
        }

        private static bool PickFloating(RaycastHit hit, ILocalSelectedPartProvider selection)
        {
            if (!ShipAim.TryPart(hit.collider, out _, out var part) || part.Settings == null)
                return false;
            Select(selection, part.Settings.id, part.Settings.fullLabel);
            _settingsId = part.Settings.id;
            _state = Settings.CopySettings.Value ? part.State : null;
            _rotation = Settings.CopyRotation.Value ? part.LocalRotation : (Quaternion?)null;
            Copied = Describe(part.State);
            return true;
        }

        private static bool PickHull(RaycastHit hit, Vector3 dir, ILocalSelectedPartProvider selection)
        {
            if (!ShipPartsTagDetection.ColliderIsHull(hit.collider) || HullPositionField == null || HullShipField == null)
                return false;
            HullHover hover;
            try
            {
                hover = new HullHover(hit, dir);
            }
            catch
            {
                return false;
            }
            var position = (HullPartPosition)HullPositionField.GetValue(hover);
            object ship = HullShipField.GetValue(hover);
            if (ship == null)
                return false;
            uint shipId = Traverse.Create(ship).Property("ShipId").GetValue<uint>();
            var part = Zoop.Aim.HullPartAt(Zoop.Aim.Ship(shipId), position);
            if (!part.HasValue || part.Value.Settings == null)
                return false;
            Forget();
            Select(selection, part.Value.Settings.id, part.Value.Settings.fullLabel);
            return true;
        }

        private static void Select(ILocalSelectedPartProvider selection, ushort id, string label)
        {
            if (Settings.PickEquipsBuildTool.Value)
                LocalPlayer.TryEquip(LocalPlayer.BuildToolItem);
            selection.SelectPart(id);
            LastResult = string.IsNullOrEmpty(label) ? "pièce " + id : label;
        }

        /// <summary>Turns the next preview of this part to the given rotation (used by circuit copy).</summary>
        public static void MatchRotation(ushort settingsId, Quaternion rotation)
        {
            _settingsId = settingsId;
            _state = null;
            _rotation = rotation;
            Copied = null;
        }

        public static void Forget()
        {
            _state = null;
            _rotation = null;
            Copied = null;
        }

        /// <summary>Applies the picked rotation once the preview of that part shows up.</summary>
        public static void Update()
        {
            if (!_rotation.HasValue || ObjectPreviewField == null)
                return;
            var preview = Zoop.Aim.Preview(ToolRefs.Build);
            if (preview == null || !preview.isActiveAndEnabled)
                return;
            var obj = ObjectPreviewField.GetValue(preview) as GameObject;
            var settings = PreviewSettingsField?.GetValue(preview) as PartSettings;
            var ship = PreviewShipField?.GetValue(preview) as IShipPartPlacements;
            if (obj == null || settings == null || ship == null || settings.id != _settingsId)
                return;
            obj.transform.rotation = ship.ToWorldRotation(_rotation.Value);
            _rotation = null;
        }

        public static void BeforeServerPlace(object[] __args)
        {
            var conn = __args.Length > 1 ? __args[1] as NetworkConnection : null;
            _placingLocal = conn != null && conn.IsLocalClient;
        }

        public static void AfterServerPlace()
        {
            _placingLocal = false;
        }

        public static void AfterAddPart(TrackedShipServer __instance, ushort __result, object[] __args)
        {
            var settings = __args.Length > 0 ? __args[0] as PartSettings : null;
            if (!_placingLocal || _state == null || settings == null || settings.id != _settingsId)
                return;
            if (!__instance.TryGetStatefulPart(__result, out var part))
                return;
            var merged = CopyState(_state, part.State);
            if (merged != null)
                __instance.UpdatePartState(__result, merged);
        }

        /// <summary>The fresh part's state with the copied part's user settings, or null when nothing applies.</summary>
        public static IStatefulPartState CopyState(IStatefulPartState from, IStatefulPartState to)
        {
            switch (to)
            {
                case ConstantState t when from is ConstantState f:
                    return t with { ConstantValue = f.ConstantValue };
                case MathGateState t when from is MathGateState f:
                    return t with { Operation = f.Operation };
                case SignalProcessorState t when from is SignalProcessorState f:
                    return t with { Operation = f.Operation };
                case SignalTransmitterState t when from is SignalTransmitterState f:
                    return t with { Channel = f.Channel };
                case BeaconState t when from is BeaconState f:
                    return t with { Name = f.Name };
                case LeverState t when from is LeverState f:
                    return t with { LeverHeight = f.LeverHeight };
                default:
                    return null;
            }
        }

        private static string Describe(IStatefulPartState state)
        {
            switch (state)
            {
                case ConstantState s: return "valeur " + s.ConstantValue;
                case MathGateState s: return s.Operation.ToString();
                case SignalProcessorState s: return s.Operation.ToString();
                case SignalTransmitterState s: return "canal " + s.Channel;
                case BeaconState s: return "nom " + s.Name;
                case LeverState s: return "levier " + s.LeverHeight;
                default: return null;
            }
        }
    }
}
