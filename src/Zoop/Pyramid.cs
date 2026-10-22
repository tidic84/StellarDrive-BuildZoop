using System.Collections.Generic;
using System.Linq;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Settings;
using UnityEngine;

namespace BuildZoop.Zoop
{
    /// <summary>
    /// A big triangle laid flat in the sloped plane of the slope corners. A lower corner panel sits in the bottom
    /// corner of its voxel and an upper corner panel in the top corner of its voxel; with the right neighbour they
    /// lie in the same diagonal plane and share an edge, so lower and upper corners tile that plane like a
    /// triangle grid. Each row is one piece narrower than the one before, going up (lower corners along the base)
    /// or going down (upper corners along the top edge).
    ///
    /// F is the lower corners' forward, e.g. (1,1,1): a lower panel in voxel p lies on F.(X - p) = -1/2 and an
    /// upper panel (forward -F) in voxel q on F.(X - q) = +1/2.
    /// </summary>
    public static class Pyramid
    {
        public sealed class Family
        {
            public HullPartSettings Lower;
            public HullPartSettings Upper;
        }

        private static Dictionary<ushort, HullPartSettings> _byId;

        public static bool IsCorner(PartSettings s) =>
            s != null && (s.snappingStyle == SnappingStyle.SlopedCorner || s.snappingStyle == SnappingStyle.SlopedCornerInv);

        /// <summary>The lower and upper slope corners of the same set as the given corner.</summary>
        public static bool TryFamily(PartSettings part, out Family family)
        {
            family = null;
            if (!(part is HullPartSettings hull) || !IsCorner(part))
                return false;
            if (_byId == null)
                _byId = Resources.FindObjectsOfTypeAll<HullPartSettings>().GroupBy(s => s.id).ToDictionary(g => g.Key, g => g.First());
            // In every set the game lists the lower corner right before the upper one.
            bool upper = part.snappingStyle == SnappingStyle.SlopedCornerInv;
            ushort lowerId = upper ? (ushort)(part.id - 1) : part.id;
            var f = new Family
            {
                Lower = Find(lowerId, SnappingStyle.SlopedCorner, hull.hullMaterialType),
                Upper = Find((ushort)(lowerId + 1), SnappingStyle.SlopedCornerInv, hull.hullMaterialType)
            };
            if (f.Lower == null || f.Upper == null)
                return false;
            family = f;
            return true;
        }

        private static HullPartSettings Find(ushort id, SnappingStyle style, HullMaterialType material)
        {
            bool Matches(HullPartSettings x) => x.snappingStyle == style && x.hullMaterialType == material;
            if (_byId.TryGetValue(id, out var s) && Matches(s))
                return s;
            // The nearest id of that kind and material belongs to the same set.
            return _byId.Values.Where(Matches).OrderBy(x => Mathf.Abs(x.id - id)).FirstOrDefault();
        }

        /// <summary>The lower corners' forward (y = +1) for a lower or an upper corner placement.</summary>
        public static Vector3Int LowerForward(HullPartPlacement anchor, PartSettings settings)
        {
            Vector3Int f = ZoopGeometry.Sign(anchor.ForwardOrientation.GetVector3());
            return settings.snappingStyle == SnappingStyle.SlopedCornerInv ? -f : f;
        }

        /// <summary>Horizontal direction along the rows (perpendicular to F seen from above).</summary>
        public static Vector3Int RowDirection(Vector3Int f) => new Vector3Int(-f.z, 0, f.x);

        /// <summary>
        /// The pieces of the triangle. anchor = the voxel of the first piece; along = +1 or -1 along the row
        /// direction; width = pieces in the first row; rows = how many rows; up = narrowing upwards (else downwards).
        /// </summary>
        public static void Build(Vector3Int anchor, bool anchorIsUpper, Vector3Int f, int along, int width, int rows, bool up,
            Family family, int limit, List<HullPartPlacement> placements, List<PartSettings> settings)
        {
            Vector3Int step = RowDirection(f) * along;
            // The horizontal axis of step that goes against F (w, F.w = -1) and the one that goes with it (w2, F.w2 = +1).
            Vector3Int w = step.x * f.x < 0 ? new Vector3Int(step.x, 0, 0) : new Vector3Int(0, 0, step.z);
            Vector3Int w2 = step - w;
            var lowerForward = Orientation(f);
            var upperForward = Orientation(-f);
            rows = Mathf.Clamp(rows, 1, width);

            if (up)
            {
                // Lower corners along the base; each upper sits between two lowers, each lower of the next row on an upper.
                Vector3Int start = anchorIsUpper ? anchor - w : anchor;
                for (int r = 0; r < rows; r++)
                {
                    Vector3Int rowStart = start + r * (w + Vector3Int.up);
                    for (int k = 0; k < width - r; k++)
                    {
                        Add(placements, settings, family.Lower, rowStart + k * step, lowerForward);
                        if (k < width - r - 1)
                            Add(placements, settings, family.Upper, rowStart + k * step + w, upperForward);
                    }
                }
            }
            else
            {
                // Upper corners along the top edge; each lower sits between two uppers, each upper of the next row under a lower.
                Vector3Int start = anchorIsUpper ? anchor : anchor - w2;
                for (int r = 0; r < rows; r++)
                {
                    Vector3Int rowStart = start + r * (w2 - Vector3Int.up);
                    for (int k = 0; k < width - r; k++)
                    {
                        Add(placements, settings, family.Upper, rowStart + k * step, upperForward);
                        if (k < width - r - 1)
                            Add(placements, settings, family.Lower, rowStart + k * step + w2, lowerForward);
                    }
                }
            }
            if (placements.Count > limit)
            {
                placements.RemoveRange(limit, placements.Count - limit);
                settings.RemoveRange(limit, settings.Count - limit);
            }
        }

        private static PartOrientation Orientation(Vector3Int v) => PartOrientationHelper.SnapAnyOrientation(new Vector3(v.x, v.y, v.z));

        private static void Add(List<HullPartPlacement> placements, List<PartSettings> settings, PartSettings part, Vector3Int coord, PartOrientation forward)
        {
            placements.Add(new HullPartPlacement(coord, VoxelPartType.Interior, forward, PartOrientationHelper.GetSlopeCornerUp(forward)));
            settings.Add(part);
        }
    }
}
