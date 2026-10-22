using System.Collections.Generic;
using Ships.Interface.Model;
using Ships.Interface.Model.Placement;
using UnityEngine;

namespace BuildZoop.Zoop
{
    /// <summary>
    /// Grid directions for a zoop. Every hull panel lies across its forward orientation (a forward wall faces +z,
    /// a ForwardRight pivoted wall runs along (1,0,-1), a ForwardUp slope rises along (0,1,-1)), so the plane of
    /// the first part is known from its placement alone.
    /// </summary>
    public static class ZoopGeometry
    {
        private static readonly Vector3Int[] Axes =
        {
            Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
        };

        private static Vector3Int[] _all;

        /// <summary>The 26 neighbour directions: axes, flat diagonals and corner diagonals.</summary>
        public static Vector3Int[] AllDirections
        {
            get
            {
                if (_all != null)
                    return _all;
                var list = new List<Vector3Int>();
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                            if (x != 0 || y != 0 || z != 0)
                                list.Add(new Vector3Int(x, y, z));
                return _all = list.ToArray();
            }
        }

        public static Vector3Int Sign(Vector3 v)
        {
            return new Vector3Int(SignOf(v.x), SignOf(v.y), SignOf(v.z));
        }

        private static int SignOf(float f)
        {
            if (f > 0.01f) return 1;
            if (f < -0.01f) return -1;
            return 0;
        }

        /// <summary>
        /// The two in-plane directions (u, v) and the out-of-plane one (w) of a hull part. Every hull panel lies
        /// across its forward orientation and along its up orientation (a wall: forward +z, up +y; a pivoted wall:
        /// forward ForwardRight, up BackRight; a slope: forward ForwardUp, up BackUp), so up gives v and up x forward gives u.
        /// </summary>
        public static void HullPlane(HullPartPlacement placement, out Vector3Int u, out Vector3Int v, out Vector3Int w)
        {
            Vector3 f = placement.ForwardOrientation.GetVector3();
            Vector3 up = placement.UpOrientation.GetVector3();
            u = Sign(Vector3.Cross(up, f));
            v = Sign(up);
            w = Sign(f);
            if (u != Vector3Int.zero && v != Vector3Int.zero && w != Vector3Int.zero)
                return;
            switch (placement.Position.Type)
            {
                case VoxelPartType.ForwardWall:
                    u = Vector3Int.right; v = Vector3Int.up; w = new Vector3Int(0, 0, 1);
                    break;
                case VoxelPartType.RightWall:
                    u = new Vector3Int(0, 0, 1); v = Vector3Int.up; w = Vector3Int.right;
                    break;
                default:
                    u = Vector3Int.right; v = new Vector3Int(0, 0, 1); w = Vector3Int.up;
                    break;
            }
        }

        public static void FloatPlane(Quaternion rotation, out Vector3Int u, out Vector3Int v, out Vector3Int w)
        {
            w = DominantAxis(rotation * Vector3.up);
            if (w.y != 0) { u = Vector3Int.right; v = new Vector3Int(0, 0, 1); }
            else if (w.x != 0) { u = new Vector3Int(0, 0, 1); v = Vector3Int.up; }
            else { u = Vector3Int.right; v = Vector3Int.up; }
            w = new Vector3Int(Mathf.Abs(w.x), Mathf.Abs(w.y), Mathf.Abs(w.z));
        }

        public static Vector3Int DominantAxis(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ax >= ay && ax >= az) return new Vector3Int(v.x >= 0 ? 1 : -1, 0, 0);
            if (ay >= az) return new Vector3Int(0, v.y >= 0 ? 1 : -1, 0);
            return new Vector3Int(0, 0, v.z >= 0 ? 1 : -1);
        }

        /// <summary>
        /// Horizontal line candidates: both ways along the panel's in-plane directions and across it, without the
        /// straight up and down ones. A slope keeps its ramp (it is the slope's own line), a pivoted wall its diagonal.
        /// </summary>
        public static List<Vector3Int> HorizontalDirections(Vector3Int u, Vector3Int v, Vector3Int w)
        {
            var list = new List<Vector3Int>();
            foreach (var d in new[] { u, v })
            {
                if (d.x == 0 && d.z == 0)
                    continue;
                Add(list, d);
                Add(list, -d);
            }
            if (w.y == 0)
            {
                Add(list, w);
                Add(list, -w);
            }
            return list;
        }

        public static readonly Vector3Int[] VerticalDirections = { Vector3Int.up, Vector3Int.down };

        private static void Add(List<Vector3Int> list, Vector3Int d)
        {
            if (d != Vector3Int.zero && !list.Contains(d))
                list.Add(d);
        }

        public static Vector3 Scale(Vector3Int offset, Vector3 step)
        {
            return new Vector3(offset.x * step.x, offset.y * step.y, offset.z * step.z);
        }

        /// <summary>
        /// Where the camera ray points along the line anchor + t * dir (dir normalised). The ray is cut with the plane
        /// that holds the line and faces the camera best, so looking down the line (zooping straight ahead) still works:
        /// the line ends where the ray meets the floor or wall it lies in. angularError is how far off the line that
        /// point is, seen from the camera.
        /// </summary>
        public static bool RayToLine(Vector3 origin, Vector3 ray, Vector3 anchor, Vector3 dir, out float t, out float angularError)
        {
            t = 0f;
            angularError = float.MaxValue;
            Vector3 best = Vector3.zero;
            float bestDot = 0f;
            foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
            {
                Vector3 n = Vector3.Cross(dir, axis);
                if (n.sqrMagnitude < 1e-4f)
                    continue;
                n.Normalize();
                float dot = Mathf.Abs(Vector3.Dot(n, ray));
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = n;
                }
            }
            if (bestDot < 1e-3f)
                return false;
            float s = Vector3.Dot(best, anchor - origin) / Vector3.Dot(best, ray);
            if (s <= 0.05f || s > 120f)
                return false;
            Vector3 hit = origin + ray * s;
            t = Vector3.Dot(hit - anchor, dir);
            angularError = (hit - (anchor + dir * t)).magnitude / s;
            return true;
        }

        public static bool RayToPlane(Vector3 origin, Vector3 ray, Vector3 anchor, Vector3 normal, out Vector3 hit)
        {
            hit = anchor;
            float den = Vector3.Dot(normal, ray);
            if (Mathf.Abs(den) < 1e-3f)
                return false;
            float s = Vector3.Dot(normal, anchor - origin) / den;
            if (s <= 0.05f || s > 120f)
                return false;
            hit = origin + ray * s;
            return true;
        }

        /// <summary>Voxel count of a hull part along each ship axis (big parts step by their size).</summary>
        public static Vector3Int HullStep(Vector3 size, PartOrientation forward)
        {
            var dir = Vector3Int.RoundToInt(size - Vector3.one);
            var r = PartOrientationHelper.RotateByOrientation(forward, dir);
            return new Vector3Int(Mathf.Abs(r.x) + 1, Mathf.Abs(r.y) + 1, Mathf.Abs(r.z) + 1);
        }
    }
}
