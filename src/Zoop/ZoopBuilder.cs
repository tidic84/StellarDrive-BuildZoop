using System.Collections.Generic;
using Managers.Interface.Model;
using Managers.Interface.Services;
using SDModKit.Game;
using SDModKit.Input;
using SDModKit.UI;
using Ships.Interface.Model;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Tools.Build;
using Tools.Recycler;
using UnityEngine;

namespace BuildZoop.Zoop
{
    /// <summary>
    /// Zoop: hold the zoop key, aim, click. The first part is the one under the cursor when the key goes down;
    /// the rest follows the camera ray, so a line keeps growing into empty space like in Satisfactory.
    /// </summary>
    public static class ZoopBuilder
    {
        private const float FloatGrid = 0.25f;
        private const int HistoryLimit = 30;

        private sealed class Batch
        {
            public uint ShipId;
            public bool Removed;
            public readonly List<HullPartPlacement> Hull = new List<HullPartPlacement>();
            public readonly List<ushort> HullSettings = new List<ushort>();
            public readonly List<FloatingPartPlacement> Floating = new List<FloatingPartPlacement>();
            public readonly List<ushort> FloatingSettings = new List<ushort>();
            public int Count => Hull.Count + Floating.Count;
        }

        private const int SendsPerFrame = 120;

        private static readonly GhostSet Ghosts = new GhostSet("BuildZoopGhosts");
        // Big zoops go out over several frames so thousands of requests do not land on the server at once.
        private static readonly Queue<System.Action> Outbox = new Queue<System.Action>();
        private static readonly List<Batch> History = new List<Batch>();

        private static bool _active;
        private static bool _toggled;
        private static bool _removing;
        private static bool _floating;
        private static uint _shipId;
        private static PartSettings _settings;

        private static HullPartPlacement _anchorHull;
        private static FloatingPartPlacement _anchorFloat;
        private static Vector3 _anchorLocal;
        private static Vector3 _step;
        private static Vector3Int _hullStep;
        private static Vector3Int _u, _v, _w;

        private static Vector3Int _dir;
        private static int _length;
        private static int _areaI, _areaJ;
        private static Vector3Int _boxEnd;
        private static Vector3 _floatCurrent;

        private static readonly List<Vector3Int> Offsets = new List<Vector3Int>();
        private static readonly List<HullPartPlacement> HullPlan = new List<HullPartPlacement>();
        private static readonly List<FloatingPartPlacement> FloatPlan = new List<FloatingPartPlacement>();
        private static readonly List<bool> Valid = new List<bool>();
        private static readonly List<HullPart> HullDoomed = new List<HullPart>();
        private static readonly List<StatefulPart> FloatDoomed = new List<StatefulPart>();
        private static int _unaffordable;

        public static bool Active => _active;
        public static bool CanUndo => History.Count > 0;
        public static string Status { get; private set; }
        public static string LastAction { get; private set; }

        public static int Count
        {
            get
            {
                if (!_active) return 0;
                if (_removing) return _floating ? FloatDoomed.Count : HullDoomed.Count;
                int n = 0;
                foreach (bool ok in Valid)
                    if (ok) n++;
                return n;
            }
        }

        public static int Sending => Outbox.Count;

        private static void Send(System.Action request)
        {
            Outbox.Enqueue(request);
        }

        /// <summary>Sends the next queued requests; runs every frame, whatever tool is out.</summary>
        public static void Pump()
        {
            for (int i = 0; i < SendsPerFrame && Outbox.Count > 0; i++)
            {
                try
                {
                    Outbox.Dequeue()();
                }
                catch (System.Exception)
                {
                    // Left the world or lost the ship: drop what is left.
                    Outbox.Clear();
                }
            }
        }

        public static void Update()
        {
            bool buildActive = ToolRefs.BuildActive;
            bool recyclerActive = ToolRefs.RecyclerActive;
            if (!buildActive && !recyclerActive)
            {
                Cancel();
                _toggled = false;
                return;
            }

            if (Settings.ModeKey != null && Settings.ModeKey.Pressed && !LocalPlayer.GameMenuOpen)
            {
                Settings.NextMode();
                _dir = Vector3Int.zero;
            }
            if (Settings.UndoKey != null && Settings.UndoKey.Pressed && !LocalPlayer.GameMenuOpen)
                Undo();

            if (!Settings.HoldToZoop.Value && Settings.ZoopKey != null && Settings.ZoopKey.Pressed)
                _toggled = !_toggled;
            bool wanted = Settings.ZoopEnabled.Value && Settings.ZoopKey != null
                && (Settings.HoldToZoop.Value ? Settings.ZoopKey.Held : _toggled);
#if DEV
            wanted |= DevHold;
#endif

            if (!wanted)
            {
                Cancel();
                Status = null;
                if (!PlacementLock.Locked && !CircuitCopy.HasSelection)
                    IdleTips(buildActive);
                return;
            }

            if (!_active)
                Begin(ToolRefs.Build, ToolRefs.Recycler, buildActive);
            if (_active)
            {
                Track(ToolRefs.Build, ToolRefs.Recycler, buildActive);
                if (buildActive)
                    HidePreview(ToolRefs.Build);
            }
            ActiveTips(recyclerActive && !buildActive);
        }

        // Renderers of the build tool's own preview, hidden while the zoop ghosts show the parts.
        private static readonly List<Renderer> HiddenPreview = new List<Renderer>();

        private static void HidePreview(BuildToolLocalClient build)
        {
            var obj = Aim.PreviewObject(build);
            if (obj == null)
                return;
            foreach (var r in obj.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled)
                    continue;
                r.enabled = false;
                HiddenPreview.Add(r);
            }
        }

        private static void ShowPreview()
        {
            foreach (var r in HiddenPreview)
                if (r != null)
                    r.enabled = true;
            HiddenPreview.Clear();
        }

        public static void Cancel()
        {
            ShowPreview();
            _active = false;
            _floating = false;
            Offsets.Clear();
            HullPlan.Clear();
            FloatPlan.Clear();
            Valid.Clear();
            HullDoomed.Clear();
            FloatDoomed.Clear();
            Ghosts.Clear();
        }

        public static void Reset()
        {
            Cancel();
            Outbox.Clear();
            _toggled = false;
            History.Clear();
            LastAction = null;
        }

        // ---------------------------------------------------------------- start

        private static void Begin(BuildToolLocalClient build, RecyclerTool recycler, bool buildActive)
        {
            if (buildActive)
            {
                var snapped = Aim.Snapped(build);
                if (snapped == null || snapped.PartSettings == null)
                {
                    Status = "visez un emplacement";
                    return;
                }
                _removing = false;
                _shipId = snapped.ShipId;
                _settings = snapped.PartSettings;
                if (snapped.FloatingPartPlacement.HasValue)
                    BeginFloating(snapped.FloatingPartPlacement.Value);
                else if (snapped.HullPartPlacement.HasValue)
                    BeginHull(snapped.HullPartPlacement.Value);
                else
                    Status = "visez un emplacement";
                return;
            }

            if (Aim.RecyclerFloating(recycler, out uint floatShip, out var floatPart))
            {
                _removing = true;
                _shipId = floatShip;
                _settings = floatPart.Settings;
                BeginFloating(new FloatingPartPlacement(floatPart.LocalPosition, PartOrientation.Forward, PartOrientation.Up));
                return;
            }
            if (Aim.RecyclerHull(recycler, out uint hullShip, out var hullPart))
            {
                _removing = true;
                _shipId = hullShip;
                _settings = hullPart.Settings;
                BeginHull(hullPart.HullPlacement);
                return;
            }
            Status = "visez une pièce";
        }

        private static void BeginHull(HullPartPlacement placement)
        {
            _floating = false;
            _anchorHull = placement;
            _anchorLocal = placement.LocalPosition;
            _hullStep = Vector3Int.one;
            if (_settings is HullPartSettings && placement.ForwardOrientation.IsSquare())
                _hullStep = ZoopGeometry.HullStep(_settings.size, placement.ForwardOrientation);
            _step = _hullStep;
            ZoopGeometry.HullPlane(placement, out _u, out _v, out _w);
            Started();
        }

        private static void BeginFloating(FloatingPartPlacement placement)
        {
            _floating = true;
            _anchorFloat = placement;
            _anchorLocal = placement.Coord;
            _floatCurrent = placement.Coord;
            _step = _removing ? Vector3.one * FloatGrid : FloatStep(_settings, placement.Rotation);
            ZoopGeometry.FloatPlane(placement.Rotation, out _u, out _v, out _w);
            Started();
        }

        private static void Started()
        {
            _active = true;
            _dir = Vector3Int.zero;
            _length = 0;
            _areaI = _areaJ = 0;
            _boxEnd = Vector3Int.zero;
            Status = null;
        }

        private static Vector3 FloatStep(PartSettings settings, Quaternion rotation)
        {
            Vector3 size = Vector3.one * 0.5f;
            try
            {
                if (settings != null && settings.part != null)
                    size = settings.Bounds.size;
            }
            catch
            {
                // Parts without bounds keep the default size.
            }
            Vector3 rotated = rotation * size;
            var step = Vector3.zero;
            for (int i = 0; i < 3; i++)
                step[i] = Mathf.Max(FloatGrid, Mathf.Ceil(Mathf.Abs(rotated[i]) / FloatGrid - 0.01f) * FloatGrid);
            return step;
        }

        // ---------------------------------------------------------------- follow the aim

        private static void Track(BuildToolLocalClient build, RecyclerTool recycler, bool buildActive)
        {
            if (!Ghosts.Begin(_shipId))
                return;
            IShipPartPlacements grid = Ghosts.Placements;
            IShipStateRead ship = Aim.Ship(_shipId);
            if (ship == null)
            {
                Ghosts.End();
                return;
            }

            if (_floating && _removing)
            {
                TrackFloatingRemoval(recycler, ship);
            }
            else
            {
                Camera cam = GameRefs.PlayerCamera;
                if (cam != null)
                {
                    Vector3 origin = grid.ToLocalPos(cam.transform.position);
                    Vector3 ray = (grid.ToLocalRotation(cam.transform.rotation) * Vector3.forward).normalized;
#if DEV
                    if (DevAim.HasValue)
                        ray = (DevAim.Value - origin).normalized;
#endif
                    switch (Settings.CurrentMode)
                    {
                        case ZoopMode.Line:
                            FollowLine(origin, ray, ZoopGeometry.HorizontalDirections(_u, _v, _w));
                            break;
                        case ZoopMode.Vertical:
                            FollowLine(origin, ray, ZoopGeometry.VerticalDirections);
                            break;
                        case ZoopMode.Diagonal:
                            FollowLine(origin, ray, ZoopGeometry.AllDirections);
                            break;
                        default:
                            if (Settings.FlatArea.Value)
                                FollowPlane(origin, ray);
                            else
                                FollowBox(build, recycler, buildActive);
                            break;
                    }
                }
                Evaluate(grid, ship);
            }

            Draw();
            Ghosts.End();
        }

        private static void FollowLine(Vector3 origin, Vector3 ray, IList<Vector3Int> directions)
        {
            Vector3Int best = Vector3Int.zero;
            float bestError = float.MaxValue, bestT = 0f, keepError = float.MaxValue, keepT = 0f;
            foreach (var d in directions)
            {
                Vector3 step = ZoopGeometry.Scale(d, _step);
                if (!ZoopGeometry.RayToLine(origin, ray, _anchorLocal, step.normalized, out float t, out float error))
                    continue;
                if (t < -0.25f * step.magnitude)
                    continue;
                float units = t / step.magnitude;
                if (d == _dir)
                {
                    keepError = error;
                    keepT = units;
                }
                if (error < bestError)
                {
                    bestError = error;
                    best = d;
                    bestT = units;
                }
            }
            // Keep the current direction unless another one is clearly closer, so the line does not flicker.
            if (_dir != Vector3Int.zero && keepError < float.MaxValue && keepError <= bestError * 1.3f + 0.01f)
            {
                best = _dir;
                bestT = keepT;
            }
            if (best == Vector3Int.zero || bestError > 1.5f)
                return;
            _dir = best;
            _length = Mathf.Clamp(Mathf.RoundToInt(bestT), 0, Mathf.Max(1, Settings.Limit.Value) - 1);

            Offsets.Clear();
            for (int k = 0; k <= _length; k++)
                Offsets.Add(_dir * k);
        }

        private static void FollowPlane(Vector3 origin, Vector3 ray)
        {
            Vector3 u = ZoopGeometry.Scale(_u, _step);
            Vector3 v = ZoopGeometry.Scale(_v, _step);
            if (ZoopGeometry.RayToPlane(origin, ray, _anchorLocal, Vector3.Cross(u, v).normalized, out Vector3 hit))
            {
                Vector3 rel = hit - _anchorLocal;
                _areaI = Mathf.RoundToInt(Vector3.Dot(rel, u) / u.sqrMagnitude);
                _areaJ = Mathf.RoundToInt(Vector3.Dot(rel, v) / v.sqrMagnitude);
            }
            FillArea(_u, _areaI, _v, _areaJ, Vector3Int.zero, 0);
        }

        private static void FollowBox(BuildToolLocalClient build, RecyclerTool recycler, bool buildActive)
        {
            Vector3? current = null;
            if (buildActive)
            {
                var snapped = Aim.Snapped(build);
                if (snapped != null && snapped.ShipId == _shipId)
                {
                    if (!_floating && snapped.HullPartPlacement.HasValue)
                        current = snapped.HullPartPlacement.Value.LocalPosition;
                    else if (_floating && snapped.FloatingPartPlacement.HasValue)
                        current = snapped.FloatingPartPlacement.Value.Coord;
                }
            }
            else if (Aim.RecyclerHull(recycler, out uint shipId, out var part) && shipId == _shipId)
            {
                current = part.HullPlacement.LocalPosition;
            }
            if (current.HasValue)
            {
                Vector3 rel = current.Value - _anchorLocal;
                _boxEnd = new Vector3Int(Mathf.RoundToInt(rel.x / _step.x), Mathf.RoundToInt(rel.y / _step.y), Mathf.RoundToInt(rel.z / _step.z));
            }
            FillArea(Vector3Int.right, _boxEnd.x, Vector3Int.up, _boxEnd.y, new Vector3Int(0, 0, 1), _boxEnd.z);
        }

        private static void FillArea(Vector3Int a, int na, Vector3Int b, int nb, Vector3Int c, int nc)
        {
            int limit = Mathf.Max(1, Settings.Limit.Value);
            Offsets.Clear();
            int sa = na < 0 ? -1 : 1, sb = nb < 0 ? -1 : 1, sc = nc < 0 ? -1 : 1;
            na = Mathf.Abs(na); nb = Mathf.Abs(nb); nc = Mathf.Abs(nc);
            // Grow outwards from the first part (ring = i + j + k) so every new panel touches one already sent.
            int maxRing = na + nb + nc;
            for (int ring = 0; ring <= maxRing && Offsets.Count < limit; ring++)
                for (int i = 0; i <= Mathf.Min(na, ring) && Offsets.Count < limit; i++)
                    for (int j = Mathf.Max(0, ring - i - nc); j <= Mathf.Min(nb, ring - i) && Offsets.Count < limit; j++)
                        Offsets.Add(a * (i * sa) + b * (j * sb) + c * ((ring - i - j) * sc));
        }

        private static void TrackFloatingRemoval(RecyclerTool recycler, IShipStateRead ship)
        {
            if (Aim.RecyclerFloating(recycler, out uint shipId, out var part) && shipId == _shipId)
                _floatCurrent = part.LocalPosition;
            Vector3 min = Vector3.Min(_anchorLocal, _floatCurrent) - Vector3.one * 0.05f;
            Vector3 max = Vector3.Max(_anchorLocal, _floatCurrent) + Vector3.one * 0.05f;
            int limit = Mathf.Max(1, Settings.Limit.Value);
            FloatDoomed.Clear();
            foreach (var p in ship.StatefulParts)
            {
                if (p.Settings == null || !(p.Placement is FloatingPartPlacement))
                    continue;
                Vector3 pos = p.LocalPosition;
                if (pos.x < min.x || pos.y < min.y || pos.z < min.z || pos.x > max.x || pos.y > max.y || pos.z > max.z)
                    continue;
                FloatDoomed.Add(p);
                if (FloatDoomed.Count >= limit)
                    break;
            }
        }

        // ---------------------------------------------------------------- what would happen

        private static void Evaluate(IShipPartPlacements grid, IShipStateRead ship)
        {
            HullPlan.Clear();
            FloatPlan.Clear();
            Valid.Clear();
            HullDoomed.Clear();
            _unaffordable = 0;

            if (_floating)
                EvaluateFloating(ship);
            else
                EvaluateHull(grid, ship);
            if (_removing)
                return;

            // Parts past what the inventory pays for turn red and are not sent.
            int affordable = Affordable(_settings);
            for (int i = 0; i < Valid.Count; i++)
            {
                if (!Valid[i])
                    continue;
                if (affordable > 0)
                {
                    affordable--;
                    continue;
                }
                Valid[i] = false;
                _unaffordable++;
            }
        }

        private static int Affordable(PartSettings settings)
        {
            var state = PlayerAccess.LocalNetworkState;
            var cost = settings == null ? null : settings.BuildingCost;
            if (state == null || state.IsUsingCheatFreeBuild || cost == null)
                return int.MaxValue;
            var counts = PlayerAccess.CurrentCounts();
            int n = int.MaxValue;
            foreach (var item in cost)
            {
                if (item.Quantity == 0)
                    continue;
                counts.TryGetValue(item.ItemId, out int have);
                n = Mathf.Min(n, have / item.Quantity);
            }
            return n;
        }

        private static void EvaluateHull(IShipPartPlacements grid, IShipStateRead ship)
        {
            var type = _anchorHull.Position.Type;
            if (type == Pairing.SharedVoxel.Second)
                type = VoxelPartType.Interior;
            var seen = new HashSet<Vector3Int>();
            foreach (var offset in Offsets)
            {
                Vector3Int coord = _anchorHull.Coord + Vector3Int.Scale(offset, _hullStep);
                var placement = _anchorHull;
                placement.Position = new HullPartPosition(coord, type);

                if (_removing)
                {
                    var part = Aim.HullPartAt(ship, placement.Position);
                    if (part.HasValue && part.Value.Settings != null && !HullDoomed.Contains(part.Value))
                        HullDoomed.Add(part.Value);
                    continue;
                }

                bool ok = true;
                if (type == VoxelPartType.Interior || type == Pairing.SharedVoxel.Second)
                {
                    // Interior parts may share a voxel with the other half of a wall + slope corner pair.
                    ok = seen.Add(coord) && Pairing.SharedVoxel.Resolve(ship, _settings, ref placement);
                }
                else
                {
                    foreach (var c in placement.CalculateAllCoords(_settings.size))
                    {
                        if (!seen.Add(c) || !grid.IsHullPartAvailable(c, type))
                            ok = false;
                    }
                }
                HullPlan.Add(placement);
                Valid.Add(ok);
            }
        }

        private static void EvaluateFloating(IShipStateRead ship)
        {
            Quaternion rotation = _anchorFloat.Rotation;
            float reach = 0f;
            foreach (var offset in Offsets)
            {
                var placement = _anchorFloat;
                placement.Coord = _anchorFloat.Coord + ZoopGeometry.Scale(offset, _step);
                FloatPlan.Add(placement);
                reach = Mathf.Max(reach, (placement.Coord - _anchorFloat.Coord).magnitude);
            }
            reach += PartGeometry.Radius(_settings) * 2f;

            var near = new List<PartBox>();
            foreach (var p in ship.StatefulParts)
            {
                if (p.Settings == null)
                    continue;
                if ((p.LocalPosition - _anchorFloat.Coord).magnitude <= reach + PartGeometry.Radius(p.Settings))
                    near.Add(PartGeometry.Box(p.Settings, p.LocalPosition, p.LocalRotation));
            }
            foreach (var placement in FloatPlan)
            {
                var box = PartGeometry.Box(_settings, placement.Coord, rotation);
                Valid.Add(!near.Exists(other => box.Overlaps(other)));
            }
        }

        private static void Draw()
        {
            if (_floating)
            {
                if (_removing)
                    foreach (var p in FloatDoomed)
                        Ghosts.Part(p.Settings, p.Placement, false);
                else
                    for (int i = 0; i < FloatPlan.Count; i++)
                        Ghosts.Part(_settings, FloatPlan[i], Valid[i]);
                return;
            }
            if (_removing)
                foreach (var part in HullDoomed)
                    Ghosts.Part(part.Settings, part.HullPlacement, false);
            else
                for (int i = 0; i < HullPlan.Count; i++)
                    Ghosts.Part(_settings, HullPlan[i], Valid[i]);
        }

        // ---------------------------------------------------------------- commit and undo

        /// <summary>Called instead of the tool's own click while zooping. True = the click was used.</summary>
        public static bool TryCommit()
        {
            if (!_active)
                return false;
            var net = GameRefs.Service<IShipInteractionNetworkService>();
            if (net == null)
            {
                Cancel();
                return false;
            }

            var batch = new Batch { ShipId = _shipId, Removed = _removing };
            if (_removing)
            {
                // Farthest first, so the panels left behind stay attached to the ship while it shrinks.
                for (int i = HullDoomed.Count - 1; i >= 0; i--)
                {
                    var part = HullDoomed[i];
                    { var request = new RemoveHullPartNetworkRequest { ShipId = _shipId, Position = part.HullPlacement.Position }; Send(() => net.RemoveHullPart(request)); }
                    batch.Hull.Add(part.HullPlacement);
                    batch.HullSettings.Add(part.Settings.id);
                }
                for (int i = FloatDoomed.Count - 1; i >= 0; i--)
                {
                    var part = FloatDoomed[i];
                    { var request = new RemoveFloatingPartNetworkRequest { ShipId = _shipId, PartId = part.Id }; Send(() => net.RemoveFloatingPart(request)); }
                    if (part.Placement is FloatingPartPlacement fp)
                    {
                        batch.Floating.Add(fp);
                        batch.FloatingSettings.Add(part.Settings.id);
                    }
                }
            }
            else
            {
                for (int i = 0; i < HullPlan.Count; i++)
                {
                    if (!Valid[i]) continue;
                    { var request = new PlaceHullPartNetworkRequest { ShipId = _shipId, SettingsId = _settings.id, Placement = HullPlan[i] }; Send(() => net.PlaceHullPart(request)); }
                    batch.Hull.Add(HullPlan[i]);
                    batch.HullSettings.Add(_settings.id);
                }
                for (int i = 0; i < FloatPlan.Count; i++)
                {
                    if (!Valid[i]) continue;
                    { var request = new PlaceFloatingPartNetworkRequest { ShipId = _shipId, SettingsId = _settings.id, Placement = FloatPlan[i] }; Send(() => net.PlaceFloatingPart(request)); }
                    batch.Floating.Add(FloatPlan[i]);
                    batch.FloatingSettings.Add(_settings.id);
                }
            }

            if (batch.Count > 0)
            {
                History.Add(batch);
                if (History.Count > HistoryLimit)
                    History.RemoveAt(0);
            }
            LastAction = (_removing ? "retiré " : "posé ") + batch.Count + (batch.Count > 1 ? " pièces" : " pièce");
            Cancel();
            if (!Settings.HoldToZoop.Value)
                _toggled = false;
            return true;
        }

        public static void Undo()
        {
            if (History.Count == 0)
            {
                LastAction = "rien à annuler";
                return;
            }
            if (Outbox.Count > 0)
            {
                LastAction = "envoi en cours, réessayez dans un instant";
                return;
            }
            var net = GameRefs.Service<IShipInteractionNetworkService>();
            var ship = Aim.Ship(History[History.Count - 1].ShipId);
            if (net == null || ship == null)
            {
                LastAction = "annulation impossible : vaisseau introuvable";
                return;
            }
            var batch = History[History.Count - 1];
            History.RemoveAt(History.Count - 1);

            int done = 0;
            // Placing is asynchronous: keep the batch if none of its parts has shown up on the ship yet.
            bool anyThere = batch.Removed;
            if (batch.Removed)
            {
                // Put the removed parts back, nearest first.
                for (int i = batch.Hull.Count - 1; i >= 0; i--, done++)
                    { var request = new PlaceHullPartNetworkRequest { ShipId = batch.ShipId, SettingsId = batch.HullSettings[i], Placement = batch.Hull[i] }; Send(() => net.PlaceHullPart(request)); }
                for (int i = batch.Floating.Count - 1; i >= 0; i--, done++)
                    { var request = new PlaceFloatingPartNetworkRequest { ShipId = batch.ShipId, SettingsId = batch.FloatingSettings[i], Placement = batch.Floating[i] }; Send(() => net.PlaceFloatingPart(request)); }
                LastAction = "annulé : " + done + " pièces remises";
                return;
            }

            for (int i = batch.Hull.Count - 1; i >= 0; i--)
            {
                var part = Aim.HullPartAt(ship, batch.Hull[i].Position);
                if (!part.HasValue || part.Value.Settings == null || part.Value.Settings.id != batch.HullSettings[i])
                    continue;
                anyThere = true;
                { var request = new RemoveHullPartNetworkRequest { ShipId = batch.ShipId, Position = batch.Hull[i].Position }; Send(() => net.RemoveHullPart(request)); }
                done++;
            }
            if (batch.Floating.Count > 0)
            {
                var parts = new List<StatefulPart>(ship.StatefulParts);
                for (int i = batch.Floating.Count - 1; i >= 0; i--)
                {
                    var match = parts.Find(p => p.Settings != null && p.Settings.id == batch.FloatingSettings[i]
                        && p.Placement is FloatingPartPlacement && (p.LocalPosition - batch.Floating[i].Coord).sqrMagnitude < 1e-4f);
                    if (match == null)
                        continue;
                    anyThere = true;
                    parts.Remove(match);
                    { var request = new RemoveFloatingPartNetworkRequest { ShipId = batch.ShipId, PartId = match.Id }; Send(() => net.RemoveFloatingPart(request)); }
                    done++;
                }
            }
            if (!anyThere)
            {
                History.Add(batch);
                LastAction = "rien à annuler pour l'instant, les pièces arrivent";
                return;
            }
            LastAction = "annulé : " + done + " pièces retirées";
        }

#if DEV
        internal static Vector3? DevAim;
        internal static bool DevHold;

        internal static void DevStart(uint shipId, PartSettings settings, HullPartPlacement anchor)
        {
            _removing = false;
            _shipId = shipId;
            _settings = settings;
            BeginHull(anchor);
        }

        internal static void DevTrack()
        {
            Track(null, null, true);
        }

        internal static string DevDescribe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("mode=").Append(Settings.CurrentMode).Append(" u=").Append(_u).Append(" v=").Append(_v).Append(" w=").Append(_w)
                .Append(" dir=").Append(_dir).Append(" step=").Append(_hullStep).Append(" count=").Append(Count).Append(" plan:");
            for (int i = 0; i < HullPlan.Count; i++)
                sb.Append(' ').Append(HullPlan[i].Coord).Append(Valid[i] ? "" : "x");
            sb.Append(" unaffordable=").Append(_unaffordable);
            return sb.ToString();
        }
#endif

        // ---------------------------------------------------------------- key tips

        private static void IdleTips(bool buildActive)
        {
            if (LocalPlayer.GameMenuOpen)
                return;
            if (!string.IsNullOrEmpty(Status))
            {
                KeyTips.Mode(Status);
                return;
            }
            if (Settings.ZoopEnabled.Value)
            {
                KeyTips.Add(Settings.ZoopKey, "Zoop (" + Settings.ModeName(Settings.CurrentMode) + ")");
                KeyTips.Add(Settings.ModeKey, "Mode");
            }
            if (CanUndo)
                KeyTips.Add(Settings.UndoKey, "Annuler");
            if (buildActive)
            {
                KeyTips.Add(PlacementLock.LockKey, "Bloquer la position");
                if (Settings.RotationLocked.Value)
                {
                    KeyTips.Mode("Rotation bloquée");
                    KeyTips.HideVanilla("rotate");
                }
                KeyTips.Add(Settings.RotationLockKey, Settings.RotationLocked.Value ? "Débloquer la rotation" : "Bloquer la rotation");
            }
        }

        private static void ActiveTips(bool removing)
        {
            if (LocalPlayer.GameMenuOpen)
                return;
            if (!_active)
            {
                if (!string.IsNullOrEmpty(Status))
                    KeyTips.Mode(Status);
                return;
            }
            string what = Settings.ModeName(Settings.CurrentMode);
            if (Settings.CurrentMode == ZoopMode.Area && !Settings.FlatArea.Value)
                what += " 3D";
            if (Outbox.Count > 0)
                what += ", envoi de " + Outbox.Count;
            KeyTips.Mode("Zoop " + what + " : " + Count + (Count > 1 ? " pièces" : " pièce")
                + (_unaffordable > 0 ? " (matériaux manquants pour " + _unaffordable + ")" : string.Empty));
            KeyTips.Add(MouseBind.Left, removing ? "Retirer" : "Poser");
            KeyTips.Add(Settings.ModeKey, "Mode");
        }
    }
}
