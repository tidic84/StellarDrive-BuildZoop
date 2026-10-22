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
    /// An area turned 45 degrees: a rectangle whose sides run along the diagonals of the grid, from the first part
    /// (one corner) to the aim (the opposite one). In diagonal coordinates s = p + q and d = p - q the area is
    /// s0 &lt; s &lt; s1, d0 &lt; d &lt; d1; the cells on its border are cut in half along their diagonal, and for flat
    /// floors those halves can be filled with floor corners, which gives straight 45 degree edges.
    /// </summary>
    public static class DiagonalArea
    {
        private static Dictionary<ushort, HullPartSettings> _byId;

        /// <summary>The flat floor and the floor corner of the same set as a floor part.</summary>
        public static bool TryFloors(PartSettings part, out HullPartSettings flat, out HullPartSettings corner)
        {
            flat = null;
            corner = null;
            if (!(part is HullPartSettings hull) || part.snappingStyle != SnappingStyle.Floor)
                return false;
            if (_byId == null)
                _byId = Resources.FindObjectsOfTypeAll<HullPartSettings>().GroupBy(s => s.id).ToDictionary(g => g.Key, g => g.First());
            flat = hull.hullMeshSegmentType == HullMeshSegmentType.Flat ? hull : Find(part.id, HullMeshSegmentType.Flat, hull.hullMaterialType);
            corner = hull.hullMeshSegmentType == HullMeshSegmentType.Corner ? hull : Find(part.id, HullMeshSegmentType.Corner, hull.hullMaterialType);
            return flat != null;
        }

        private static HullPartSettings Find(ushort id, HullMeshSegmentType segment, HullMaterialType material)
        {
            // The nearest id of that kind and material belongs to the same set.
            return _byId.Values.Where(x => x.snappingStyle == SnappingStyle.Floor && x.hullMeshSegmentType == segment && x.hullMaterialType == material)
                .OrderBy(x => Mathf.Abs(x.id - id)).FirstOrDefault();
        }

        /// <summary>
        /// The cells of the area. pAxis / qAxis = the two grid directions of the plane, (P, Q) = the aim in them.
        /// Full cells use full; with edges (flat floors only) the half cells get a floor corner covering the inside half.
        /// </summary>
        public static void Build(HullPartPlacement anchor, Vector3Int pAxis, Vector3Int qAxis, int P, int Q, PartSettings full,
            HullPartSettings edges, int limit, List<HullPartPlacement> placements, List<PartSettings> settings)
        {
            int sT = P + Q, dT = P - Q;
            // One cell of margin around anchor and aim, so both are whole cells.
            int s0 = Mathf.Min(0, sT) - 1, s1 = Mathf.Max(0, sT) + 1;
            int d0 = Mathf.Min(0, dT) - 1, d1 = Mathf.Max(0, dT) + 1;
            var cells = new List<(int ring, HullPartPlacement placement, PartSettings part)>();
            for (int s = s0; s <= s1; s++)
            {
                for (int d = d0; d <= d1; d++)
                {
                    if (((s + d) & 1) != 0)
                        continue;
                    int p = (s + d) / 2, q = (s - d) / 2;
                    Vector3Int coord = anchor.Coord + pAxis * p + qAxis * q;
                    bool sInside = s > s0 && s < s1, dInside = d > d0 && d < d1;
                    if (sInside && dInside)
                    {
                        var placement = anchor;
                        placement.Position = new HullPartPosition(coord, anchor.Position.Type);
                        cells.Add((Mathf.Abs(p) + Mathf.Abs(q), placement, full));
                        continue;
                    }
                    // A border cell cut in half; the four tips (on two borders at once) are cut to a quarter and left out.
                    if (edges == null || sInside == dInside)
                        continue;
                    PartOrientation up = !sInside ? (s == s1 ? PartOrientation.Right : PartOrientation.Left)
                        : (d == d1 ? PartOrientation.Back : PartOrientation.Forward);
                    cells.Add((Mathf.Abs(p) + Mathf.Abs(q), new HullPartPlacement(coord, VoxelPartType.Floor, PartOrientation.Up, up), edges));
                }
            }
            // Grow outwards from the first part.
            foreach (var cell in cells.OrderBy(c => c.ring).Take(limit))
            {
                placements.Add(cell.placement);
                settings.Add(cell.part);
            }
        }
    }
}
