using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Managers.Interface;
using SDModKit.Util;
using Ships.Grid;
using Ships.Grid.Model;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Tools.Build.Model;
using Tools.Common.Hover;
using UnityEngine;

namespace BuildZoop.Pairing
{
    /// <summary>
    /// A pivoted wall and a slope corner both live in a voxel's single interior slot, so the game refuses a slope
    /// corner next to a diagonal wall although it takes one next to a straight wall. They do not overlap (the corner
    /// only touches the wall along its bottom or top edge), so this adds a second interior slot, voxel part type 4,
    /// used only for such a pair: the second part of the pair goes there.
    ///
    /// The second slot is kept out of world.json (in buildzoop-hull.txt next to it), so a world still loads without
    /// the mod; those parts are simply missing then. In multiplayer every player needs the mod.
    /// </summary>
    public static class SharedVoxel
    {
        public const VoxelPartType Second = (VoxelPartType)4;
        private const string SaveFile = "buildzoop-hull.txt";

        private sealed class Slot
        {
            public HullPart? Part;
        }

        private static readonly ConditionalWeakTable<ShipVoxel, Slot> Slots = new ConditionalWeakTable<ShipVoxel, Slot>();
        private static readonly MethodInfo HasPartSetter = AccessTools.PropertySetter(typeof(ShipVoxel), nameof(ShipVoxel.HasPart));
        private static readonly FieldInfo HoverPosition = AccessTools.Field(typeof(HullHover), "_hullPartPosition");
        private static readonly FieldInfo HoverShip = AccessTools.Field(typeof(HullHover), "_currentShip");

        // The part the build preview is snapping, while the game looks for a spot for it.
        private static PartSettings _previewing;

        // Second-slot parts taken out of the world file being written: ship id -> parts.
        private static readonly Dictionary<uint, List<string>> Stash = new Dictionary<uint, List<string>>();

        public static void Apply(HarmonyLib.Harmony harmony, Log log)
        {
            int failed = 0;
            void Patch(Type type, string method, string prefix = null, string postfix = null, Type[] args = null)
            {
                var target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                if (target == null)
                {
                    failed++;
                    log.Warn($"{type.Name}.{method} not found");
                    return;
                }
                harmony.Patch(target,
                    prefix == null ? null : new HarmonyMethod(typeof(SharedVoxel), prefix),
                    postfix == null ? null : new HarmonyMethod(typeof(SharedVoxel), postfix));
            }

            // Storage: the slot itself.
            Patch(typeof(ShipVoxel), nameof(ShipVoxel.GetPartFromType), nameof(BeforeGetPart));
            Patch(typeof(ShipVoxel), nameof(ShipVoxel.SetPart), nameof(BeforeSetPart), nameof(AfterSetPart));
            // Everything that switches on the voxel part type treats the second slot as the interior one.
            Patch(AccessTools.TypeByName("Ships.Parts.Common.Utils.PartColliderUtils"), "GetPartColliderTag", nameof(AsInteriorType));
            var mesh = AccessTools.TypeByName("Ships.Services.HullMeshBuilder");
            Patch(mesh, "ScanEdgesFlags", nameof(AsInteriorPart));
            Patch(mesh, "GetHullPlaneOrientation", nameof(AsInteriorPlacement));
            // Building: accept the pair while the preview snaps, and send the second part to the second slot.
            Patch(typeof(ShipGrid), nameof(ShipGrid.IsPartAvailable), postfix: nameof(AfterIsAvailable));
            Patch(AccessTools.TypeByName("Tools.Build.PartPreviewDetection"), "CalculateSnapDetectionUpdate", nameof(BeforeSnap), nameof(AfterSnap));
            // Aiming (recycler, paint, pick): the collider tag tells which part of the pair is under the cursor.
            var hover = AccessTools.Constructor(typeof(HullHover), new[] { typeof(RaycastHit), typeof(Vector3) });
            if (hover != null)
                harmony.Patch(hover, postfix: new HarmonyMethod(typeof(SharedVoxel), nameof(AfterHover)));
            else
                failed++;
            // Saving.
            var ships = AccessTools.TypeByName("Saving.Services.Converters.ShipsConverter");
            var saver = AccessTools.TypeByName("Saving.Services.GameSaver");
            Patch(ships, "ConvertShipStateToSerializable", postfix: nameof(AfterShipToSave));
            Patch(ships, "ConvertToState", nameof(BeforeShipsFromSave));
            Patch(saver, "SaveGame", nameof(BeforeSave), nameof(AfterSave), new[] { typeof(string) });
            if (failed > 0)
                log.Warn("diagonal wall + slope corner pairing may not work fully (" + failed + " hooks missing)");
        }

        // ---------------------------------------------------------------- which parts pair up

        private static bool IsPivotedWall(PartOrientation forward)
        {
            return forward == PartOrientation.ForwardRight || forward == PartOrientation.ForwardLeft
                || forward == PartOrientation.BackRight || forward == PartOrientation.BackLeft;
        }

        private static bool IsSlopeCorner(PartSettings settings)
        {
            return settings != null && (settings.snappingStyle == SnappingStyle.SlopedCorner || settings.snappingStyle == SnappingStyle.SlopedCornerInv);
        }

        private static bool IsSlopeCorner(HullPart part)
        {
            return part.HullSettings != null && part.HullSettings.hullMeshSegmentType == HullMeshSegmentType.SlopeCorner;
        }

        /// <summary>A pivoted wall and a slope corner whose sloped side runs along the wall (the game's own rule for a corner on top of a wall).</summary>
        private static bool Fits(PartSettings settings, HullPartPlacement placement, HullPart other)
        {
            bool wall = IsPivotedWall(placement.ForwardOrientation) && !IsSlopeCorner(settings);
            bool corner = IsSlopeCorner(settings);
            bool otherWall = IsPivotedWall(other.ForwardOrientation) && !IsSlopeCorner(other);
            bool otherCorner = IsSlopeCorner(other);
            if (!(wall && otherCorner) && !(corner && otherWall))
                return false;
            float dot = Vector3.Dot(placement.ForwardOrientation.GetVector3().normalized, other.ForwardOrientation.GetVector3().normalized);
            return Mathf.Abs(dot) >= 0.5f;
        }

        private static bool Pairable(PartSettings settings)
        {
            if (settings == null)
                return false;
            if (IsSlopeCorner(settings))
                return true;
            return settings.snappingStyle == SnappingStyle.WallPivotable;
        }

        /// <summary>
        /// Where an interior part really goes: the interior slot when it is free (and fits what is in the second
        /// one), the second slot when the interior one holds the other half of a pair. False = nowhere.
        /// </summary>
        public static bool Resolve(IShipStateRead ship, PartSettings settings, ref HullPartPlacement placement)
        {
            var type = placement.Position.Type;
            if (type != VoxelPartType.Interior && type != Second)
                return true;
            var coord = placement.Coord;
            ShipVoxel voxel = ship?.GetVoxel(coord);
            HullPart? first = voxel?.GetPartFromType(VoxelPartType.Interior);
            HullPart? second = voxel == null ? null : Get(voxel);
            if (!first.HasValue)
            {
                if (second.HasValue && !Fits(settings, placement, second.Value))
                    return false;
                placement.Position = new HullPartPosition(coord, VoxelPartType.Interior);
                return true;
            }
            if (second.HasValue || !Fits(settings, placement, first.Value))
                return false;
            placement.Position = new HullPartPosition(coord, Second);
            return true;
        }

        // ---------------------------------------------------------------- storage

        private static HullPart? Get(ShipVoxel voxel)
        {
            return Slots.TryGetValue(voxel, out var slot) ? slot.Part : null;
        }

        public static bool BeforeGetPart(ShipVoxel __instance, VoxelPartType type, ref HullPart? __result)
        {
            if (type != Second)
                return true;
            __result = Get(__instance);
            return false;
        }

        public static bool BeforeSetPart(ShipVoxel __instance, VoxelPartType type, HullPart? hullPart)
        {
            if (type != Second)
                return true;
            Slots.GetOrCreateValue(__instance).Part = hullPart;
            HasPartSetter?.Invoke(__instance, new object[] { __instance.Floor.HasValue || __instance.ForwardWall.HasValue
                || __instance.RightWall.HasValue || __instance.InteriorPart.HasValue || hullPart.HasValue });
            return false;
        }

        public static void AfterSetPart(ShipVoxel __instance)
        {
            if (!__instance.HasPart && Get(__instance).HasValue)
                HasPartSetter?.Invoke(__instance, new object[] { true });
        }

        public static void AsInteriorType(ref VoxelPartType partType)
        {
            if (partType == Second)
                partType = VoxelPartType.Interior;
        }

        public static void AsInteriorPart(ref HullPart part)
        {
            if (part.HullPlacement.Position.Type != Second)
                return;
            var placement = part.HullPlacement;
            placement.Position = new HullPartPosition(placement.Coord, VoxelPartType.Interior);
            part = new HullPart(part.HullSettings, placement, part.Color);
        }

        public static void AsInteriorPlacement(ref HullPartPlacement placement)
        {
            if (placement.Position.Type == Second)
                placement.Position = new HullPartPosition(placement.Coord, VoxelPartType.Interior);
        }

        // ---------------------------------------------------------------- build preview

        public static void BeforeSnap(SnapDetectionState state)
        {
            _previewing = Pairable(state.partSettings) ? state.partSettings : null;
        }

        public static void AfterIsAvailable(ShipGrid __instance, HullPartPosition hullPartPosition, ref bool __result)
        {
            if (__result || _previewing == null || hullPartPosition.Type != VoxelPartType.Interior)
                return;
            // Let the game's snap detectors offer the spot; AfterSnap checks the pair really fits.
            var voxel = __instance.GetVoxel(hullPartPosition.VoxelCoord);
            var first = voxel?.InteriorPart;
            if (first.HasValue && !Get(voxel).HasValue && (IsSlopeCorner(_previewing) ? !IsSlopeCorner(first.Value) : IsSlopeCorner(first.Value)))
                __result = true;
        }

        public static void AfterSnap(SnapDetectionState state, ref SnapDetectionResults __result)
        {
            var settings = _previewing;
            _previewing = null;
            if (settings == null || !__result.IsSnapped || !(__result.placement is HullPartPlacement placement) || __result.shipPartsProxy == null)
                return;
            if (placement.Position.Type != VoxelPartType.Interior)
                return;
            var ship = Zoop.Aim.Ship(__result.shipPartsProxy.ShipId);
            // The preview shows the pivoted version of a wall once it snaps diagonally.
            var real = settings is HullPartSettings hull && hull.pivotedSettings != null && IsPivotedWall(placement.ForwardOrientation) ? hull.pivotedSettings : settings;
            if (Resolve(ship, real, ref placement))
            {
                __result.placement = placement;
                return;
            }
            __result.IsSnapped = false;
            __result.placement = null;
            __result.shipPartsProxy = null;
        }

        /// <summary>Points the hover at the second slot when the collider under the cursor is that part's.</summary>
        public static void AfterHover(HullHover __instance, RaycastHit hit)
        {
            if (HoverPosition == null || HoverShip == null || hit.collider == null)
                return;
            var position = (HullPartPosition)HoverPosition.GetValue(__instance);
            if (position.Type != VoxelPartType.Interior)
                return;
            var targeter = HoverShip.GetValue(__instance);
            if (targeter == null)
                return;
            uint shipId = Traverse.Create(targeter).Property("ShipId").GetValue<uint>();
            var voxel = Zoop.Aim.Ship(shipId)?.GetVoxel(position.VoxelCoord);
            var second = voxel == null ? null : Get(voxel);
            if (!second.HasValue)
                return;
            var first = voxel.InteriorPart;
            string tag = hit.collider.tag;
            string secondTag = Tag(second.Value);
            if (tag == secondTag && (!first.HasValue || Tag(first.Value) != tag))
                HoverPosition.SetValue(__instance, new HullPartPosition(position.VoxelCoord, Second));
        }

        private static readonly MethodInfo TagMethod = AccessTools.Method(AccessTools.TypeByName("Ships.Parts.Common.Utils.PartColliderUtils"), "GetPartColliderTag");

        private static string Tag(HullPart part)
        {
            return TagMethod?.Invoke(null, new object[] { VoxelPartType.Interior, part.ForwardOrientation }) as string;
        }

        // ---------------------------------------------------------------- saving

        public static void BeforeSave()
        {
            Stash.Clear();
        }

        /// <summary>Takes the second-slot parts out of the ship written to world.json.</summary>
        public static void AfterShipToSave(object __result)
        {
            var t = Traverse.Create(__result);
            var parts = t.Field("hullParts").GetValue() as Array;
            if (parts == null)
                return;
            uint id = t.Field("id").GetValue<uint>();
            var keep = new List<object>();
            foreach (object p in parts)
            {
                var part = Traverse.Create(p);
                if (part.Field("voxelPartTypeId").GetValue<byte>() != (byte)Second)
                {
                    keep.Add(p);
                    continue;
                }
                if (!Stash.TryGetValue(id, out var list))
                    Stash[id] = list = new List<string>();
                var front = part.Field("FrontColor");
                var back = part.Field("BackColor");
                list.Add(string.Join(" ", part.Field("partId").GetValue<ushort>(), part.Field("x").GetValue<int>(), part.Field("y").GetValue<int>(),
                    part.Field("z").GetValue<int>(), part.Field("forwardOrientationId").GetValue<byte>(), part.Field("upOrientationId").GetValue<byte>(),
                    front.Field("x").GetValue<byte>(), front.Field("y").GetValue<byte>(), front.Field("z").GetValue<byte>(),
                    back.Field("x").GetValue<byte>(), back.Field("y").GetValue<byte>(), back.Field("z").GetValue<byte>()));
            }
            if (keep.Count == parts.Length)
                return;
            var trimmed = Array.CreateInstance(parts.GetType().GetElementType(), keep.Count);
            for (int i = 0; i < keep.Count; i++)
                trimmed.SetValue(keep[i], i);
            t.Field("hullParts").SetValue(trimmed);
        }

        public static void AfterSave(string worldDirectoryFullName)
        {
            try
            {
                string path = Path.Combine(worldDirectoryFullName, SaveFile);
                if (Stash.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }
                var lines = new List<string> { "# Build Zoop: slope corners sharing a voxel with a diagonal wall. ship partId x y z forward up front(rgb) back(rgb)" };
                foreach (var kv in Stash)
                    lines.AddRange(kv.Value.Select(l => kv.Key + " " + l));
                File.WriteAllLines(path, lines);
            }
            finally
            {
                Stash.Clear();
            }
        }

        /// <summary>Puts the second-slot parts back into the ships read from world.json.</summary>
        public static void BeforeShipsFromSave(object ships)
        {
            string world = MainGameSceneLoadData.SaveFileFullName;
            string dir = string.IsNullOrEmpty(world) ? null : Path.GetDirectoryName(world);
            string path = dir == null ? null : Path.Combine(dir, SaveFile);
            if (path == null || !File.Exists(path) || !(ships is Array array))
                return;
            var byShip = new Dictionary<uint, List<string[]>>();
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line))
                    continue;
                string[] p = line.Split(' ');
                if (p.Length < 13 || !uint.TryParse(p[0], out uint ship))
                    continue;
                if (!byShip.TryGetValue(ship, out var list))
                    byShip[ship] = list = new List<string[]>();
                list.Add(p);
            }
            foreach (object ship in array)
            {
                var t = Traverse.Create(ship);
                if (!byShip.TryGetValue(t.Field("id").GetValue<uint>(), out var extra))
                    continue;
                var parts = t.Field("hullParts").GetValue() as Array;
                Type partType = parts.GetType().GetElementType();
                var grown = Array.CreateInstance(partType, parts.Length + extra.Count);
                Array.Copy(parts, grown, parts.Length);
                for (int i = 0; i < extra.Count; i++)
                {
                    string[] p = extra[i];
                    object part = Activator.CreateInstance(partType);
                    var pt = Traverse.Create(part);
                    pt.Field("partId").SetValue(ushort.Parse(p[1]));
                    pt.Field("x").SetValue(int.Parse(p[2]));
                    pt.Field("y").SetValue(int.Parse(p[3]));
                    pt.Field("z").SetValue(int.Parse(p[4]));
                    pt.Field("forwardOrientationId").SetValue(byte.Parse(p[5]));
                    pt.Field("upOrientationId").SetValue(byte.Parse(p[6]));
                    pt.Field("voxelPartTypeId").SetValue((byte)Second);
                    pt.Field("FrontColor").SetValue(Color(pt.Field("FrontColor").GetValue(), p, 7));
                    pt.Field("BackColor").SetValue(Color(pt.Field("BackColor").GetValue(), p, 10));
                    grown.SetValue(part, parts.Length + i);
                }
                t.Field("hullParts").SetValue(grown);
            }
        }

        private static object Color(object boxed, string[] p, int at)
        {
            var c = Traverse.Create(boxed);
            c.Field("x").SetValue(byte.Parse(p[at]));
            c.Field("y").SetValue(byte.Parse(p[at + 1]));
            c.Field("z").SetValue(byte.Parse(p[at + 2]));
            return boxed;
        }
    }
}
