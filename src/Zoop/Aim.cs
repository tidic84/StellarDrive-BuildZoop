using System.Reflection;
using HarmonyLib;
using SDModKit.Game;
using Ships.Grid.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Tools.Build;
using Tools.Build.Model;
using Tools.Common.Hover;
using Tools.Recycler;
using UnityEngine;

namespace BuildZoop.Zoop
{
    /// <summary>What the build tool preview or the recycler is pointing at.</summary>
    public static class Aim
    {
        private static readonly FieldInfo PreviewField = AccessTools.Field(typeof(BuildToolLocalClient), "_partPreview");
        private static readonly FieldInfo ObjectPreviewField = AccessTools.Field(typeof(PartPreview), "_objectPreview");
        private static readonly FieldInfo HoveredField = AccessTools.Field(typeof(RecyclerTool), "hoveredObject");
        private static readonly FieldInfo HullPositionField = AccessTools.Field(typeof(HullHover), "_hullPartPosition");
        private static readonly FieldInfo HullShipField = AccessTools.Field(typeof(HullHover), "_currentShip");
        private static readonly FieldInfo FloatColliderField = AccessTools.Field(typeof(FloatingPartHover), "_partCollider");

        public static PartPreview Preview(BuildToolLocalClient build)
        {
            if (build == null || PreviewField == null)
                return null;
            return PreviewField.GetValue(build) as PartPreview;
        }

        /// <summary>The snapped placement the build tool would make now, or null.</summary>
        public static SnappedPreviewPlacement Snapped(BuildToolLocalClient build)
        {
            var preview = Preview(build);
            if (preview == null || ObjectPreviewField == null)
                return null;
            var obj = ObjectPreviewField.GetValue(preview) as GameObject;
            if (obj == null || !obj.activeInHierarchy)
                return null;
            try
            {
                return preview.GetPlacement() as SnappedPreviewPlacement;
            }
            catch
            {
                return null;
            }
        }

        public static IShipStateRead Ship(uint shipId)
        {
            var ships = GameRefs.Service<IShipsProvider>();
            IShipStateRead ship = null;
            if (ships == null || !ships.TryGetShip(shipId, out ship))
                return null;
            return ship;
        }

        public static HullPart? HullPartAt(IShipStateRead ship, HullPartPosition position)
        {
            ShipVoxel voxel = ship?.GetVoxel(position.VoxelCoord);
            return voxel?.GetPartFromType(position.Type);
        }

        /// <summary>The hull part under the recycler.</summary>
        public static bool RecyclerHull(RecyclerTool recycler, out uint shipId, out HullPart part)
        {
            shipId = 0;
            part = default;
            if (recycler == null || HoveredField == null || HullPositionField == null || HullShipField == null)
                return false;
            if (!(HoveredField.GetValue(recycler) is HullHover hover))
                return false;
            var position = (HullPartPosition)HullPositionField.GetValue(hover);
            object ship = HullShipField.GetValue(hover);
            if (ship == null)
                return false;
            shipId = Traverse.Create(ship).Property("ShipId").GetValue<uint>();
            var found = HullPartAt(Ship(shipId), position);
            if (!found.HasValue || found.Value.Settings == null)
                return false;
            part = found.Value;
            return true;
        }

        /// <summary>The floating part under the recycler.</summary>
        public static bool RecyclerFloating(RecyclerTool recycler, out uint shipId, out StatefulPart part)
        {
            shipId = 0;
            part = null;
            if (recycler == null || HoveredField == null || FloatColliderField == null)
                return false;
            if (!(HoveredField.GetValue(recycler) is FloatingPartHover hover))
                return false;
            var collider = FloatColliderField.GetValue(hover) as Collider;
            if (collider == null || !ShipAim.TryPart(collider, out var ship, out part))
                return false;
            shipId = ship.Id;
            return true;
        }
    }
}
