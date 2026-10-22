using System.Collections.Generic;
using System.Linq;
using Players.Interface.Services;
using SDModKit.Game;
using SDModKit.UI;
using Ships;
using Ships.Cables;
using Ships.Interface.Model.Parts;
using Ships.Interface.Model.Parts.Common;
using Ships.Interface.Model.Parts.StateTypes;
using Ships.Interface.Model.Placement;
using Ships.Interface.Model.State;
using Ships.Interface.Services;
using Ships.Interface.Settings;
using Ships.Services;
using Tools.Build;
using UnityEngine;

namespace BuildZoop
{
    /// <summary>
    /// Zoop key + middle click picks floating parts one by one (with the cables between them); releasing the zoop key
    /// arms the paste, which follows the build preview of the first part. Pasting runs on the host.
    /// </summary>
    public static class CircuitCopy
    {
        private const float Reach = 12f;
        private const int HistoryLimit = 20;

        private sealed class Item
        {
            public ushort Id;
            public PartSettings Settings;
            public IPartPlacement Placement;
            public Vector3 Position;
            public Quaternion Rotation;
            public IStatefulPartState State;
        }

        private struct Cable
        {
            public int A;
            public byte SocketA;
            public int B;
            public byte SocketB;
            public CableType Type;
        }

        private sealed class Paste
        {
            public uint ShipId;
            public readonly List<ushort> Parts = new List<ushort>();
            public Dictionary<uint, int> Charged;
        }

        private static readonly List<Item> Items = new List<Item>();
        private static readonly List<Cable> Cables = new List<Cable>();
        private static readonly List<Paste> History = new List<Paste>();
        private static readonly List<FloatingPartPlacement> Plan = new List<FloatingPartPlacement>();
        private static readonly List<bool> PlanValid = new List<bool>();
        private static readonly GhostSet SelectGhosts = new GhostSet("BuildZoopCopySelect");
        private static readonly GhostSet PasteGhosts = new GhostSet("BuildZoopCopyPaste");

        private static uint _sourceShip;
        private static uint _targetShip;
        private static bool _needsArm;
        private static bool _armed;
        private static string _blocked;

        public static bool HasSelection => Items.Count > 0;
        public static bool CanUndo => History.Count > 0;
        public static string Status { get; private set; }

        public static string Describe()
        {
            return Items.Count + (Items.Count == 1 ? " pièce, " : " pièces, ") + Cables.Count + (Cables.Count == 1 ? " câble" : " câbles");
        }

        public static void Click()
        {
            if (LocalPlayer.GameMenuOpen)
                return;
            if (!ShipAim.TryGetAimedPart(Reach, out var ship, out var part) || part.Settings == null)
            {
                if (HasSelection)
                    Clear();
                return;
            }
            if (!(part.Placement is FloatingPartPlacement))
                return;
            if (HasSelection && ship.Id != _sourceShip)
                Clear();
            _sourceShip = ship.Id;
            int index = Items.FindIndex(i => i.Id == part.Id);
            if (index >= 0)
                Items.RemoveAt(index);
            else
                Items.Add(new Item { Id = part.Id });
            Refresh(ship);
            _needsArm = true;
        }

        public static void Update(BuildToolLocalClient build, bool selecting)
        {
            _armed = false;
            if (!HasSelection)
            {
                SelectGhosts.Clear();
                PasteGhosts.Clear();
                return;
            }
            if (selecting)
            {
                PasteGhosts.Clear();
                DrawSelection();
                if (!LocalPlayer.GameMenuOpen)
                {
                    KeyTips.Mode("Copie : " + Describe());
                    KeyTips.Add(Settings.PickKey, "Ajouter / retirer (vide = tout effacer)");
                    KeyTips.Add(Settings.ZoopKey, "Relâcher pour coller");
                }
                return;
            }
            SelectGhosts.Clear();
            if (build == null || !build.isActiveAndEnabled)
            {
                PasteGhosts.Clear();
                return;
            }
            if (_needsArm)
            {
                _needsArm = false;
                Arm();
            }
            if (!LocalPlayer.GameMenuOpen)
            {
                KeyTips.Mode("Coller : " + Describe());
                KeyTips.Add(Settings.ZoopKey, "Modifier la copie");
            }
            var first = Items[0];
            var snapped = BuildPreview.Current();
            if (snapped == null || !snapped.FloatingPartPlacement.HasValue || snapped.PartSettings == null || snapped.PartSettings.id != first.Settings.id)
            {
                PasteGhosts.Clear();
                return;
            }
            _targetShip = snapped.ShipId;
            BuildPlan(snapped.FloatingPartPlacement.Value);
            DrawPaste();
            _armed = true;
            if (_blocked != null && !LocalPlayer.GameMenuOpen)
                KeyTips.Mode("Collage bloqué : " + _blocked);
        }

        /// <summary>Called on the build tool's click. True = the click was used.</summary>
        public static bool TryPaste()
        {
            if (!_armed)
                return false;
            if (_blocked != null)
                return true;
            var ships = GameRefs.Service<ShipsServerTracker>();
            var cables = GameRefs.Service<CablesServerTracker>();
            var target = ships == null ? null : ships.GetShip(_targetShip);
            var source = ships == null ? null : ships.GetShip(_sourceShip);
            if (target == null || cables == null)
                return true;
            if (!BuildCost.TryCharge(Items.Select(i => i.Settings).ToList(), out var charged, out _))
                return true;

            var paste = new Paste { ShipId = _targetShip, Charged = charged };
            var ids = new ushort[Items.Count];
            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                ids[i] = target.AddStatefulPart(item.Settings, Plan[i]);
                paste.Parts.Add(ids[i]);
                // The source part may have been changed since it was picked: copy what it holds now.
                var state = item.State;
                if (source != null && source.TryGetStatefulPart(item.Id, out var live) && live.Settings != null && live.Settings.id == item.Settings.id)
                    state = live.State;
                if (state != null && target.TryGetStatefulPart(ids[i], out var fresh))
                {
                    var merged = Pick.CopyState(state, fresh.State);
                    if (merged != null)
                        target.UpdatePartState(ids[i], merged);
                }
            }

            int linked = 0;
            foreach (var cable in Cables)
            {
                var state = new CableState
                {
                    Type = cable.Type,
                    PlugA = new CablePlugSocket { EntityPartSocket = new EntityPartSocket(_targetShip, ids[cable.A], cable.SocketA) },
                    PlugB = new CablePlugSocket { EntityPartSocket = new EntityPartSocket(_targetShip, ids[cable.B], cable.SocketB) }
                };
                if (cables.TryGetCableState(cables.AddCable(state), out _))
                    linked++;
            }

            History.Add(paste);
            if (History.Count > HistoryLimit)
                History.RemoveAt(0);
            Status = "collé " + ids.Length + " pièces et " + linked + " câbles"
                + (linked < Cables.Count ? ", " + (Cables.Count - linked) + " câbles ratés" : string.Empty);
            return true;
        }

        public static void Undo()
        {
            if (!CanUndo)
                return;
            if (!GameRefs.IsServer)
            {
                Status = "annuler demande d'être l'hôte";
                return;
            }
            var paste = History[History.Count - 1];
            History.RemoveAt(History.Count - 1);
            var ships = GameRefs.Service<ShipsServerTracker>();
            var cables = GameRefs.Service<CablesServerTracker>();
            var ship = ships == null ? null : ships.GetShip(paste.ShipId);
            if (ship == null || cables == null)
            {
                Status = "vaisseau introuvable sur le serveur";
                return;
            }
            int removed = 0;
            foreach (ushort id in paste.Parts)
            {
                if (!ship.TryGetStatefulPart(id, out _))
                    continue;
                foreach (var cable in cables.GetCablesConnectedToPart(new PartContext(paste.ShipId, id)).ToList())
                    cables.DeleteCable(cable.Id);
                ship.RemoveStatefulPart(id);
                removed++;
            }
            BuildCost.Refund(paste.Charged);
            Status = "retiré " + removed + " pièces collées";
        }

        public static void Clear()
        {
            Items.Clear();
            Cables.Clear();
            Plan.Clear();
            PlanValid.Clear();
            SelectGhosts.Clear();
            PasteGhosts.Clear();
            _needsArm = false;
            _armed = false;
            _blocked = null;
        }

        public static void Reset()
        {
            Clear();
            History.Clear();
            Status = null;
        }

        private static IShipStateRead Ship(uint id)
        {
            return Zoop.Aim.Ship(id);
        }

        /// <summary>Re-reads the picked parts from the ship and collects the cables running between them.</summary>
        private static void Refresh(IShipStateRead ship)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                if (!ship.TryGetStatefulPart(Items[i].Id, out var part) || part.Settings == null || !(part.Placement is FloatingPartPlacement))
                {
                    Items.RemoveAt(i);
                    continue;
                }
                var item = Items[i];
                item.Settings = part.Settings;
                item.Placement = part.Placement;
                item.Position = part.LocalPosition;
                item.Rotation = part.LocalRotation;
                item.State = part.State;
            }
            Cables.Clear();
            var tracker = GameRefs.Service<CablesClientTracker>();
            if (tracker == null || Items.Count == 0)
                return;
            var index = new Dictionary<ushort, int>();
            for (int i = 0; i < Items.Count; i++)
                index[Items[i].Id] = i;
            foreach (var cable in tracker.GetShipCables(_sourceShip))
            {
                var a = cable.State.PlugA;
                var b = cable.State.PlugB;
                if (a.IsHeldByPlayer || b.IsHeldByPlayer || a.EntityId != _sourceShip || b.EntityId != _sourceShip)
                    continue;
                if (!index.TryGetValue(a.EntityPartSocket.Socket.PartId, out int ia) || !index.TryGetValue(b.EntityPartSocket.Socket.PartId, out int ib))
                    continue;
                Cables.Add(new Cable
                {
                    A = ia,
                    SocketA = a.EntityPartSocket.Socket.SocketId,
                    B = ib,
                    SocketB = b.EntityPartSocket.Socket.SocketId,
                    Type = cable.State.Type
                });
            }
        }

        /// <summary>Selects the first part in the build tool with its rotation, so its preview carries the paste.</summary>
        private static void Arm()
        {
            var ship = Ship(_sourceShip);
            if (ship != null)
                Refresh(ship);
            if (!HasSelection)
                return;
            var selection = GameRefs.Service<ILocalSelectedPartProvider>();
            if (selection == null)
                return;
            selection.SelectPart(Items[0].Settings.id);
            Pick.MatchRotation(Items[0].Settings.id, Items[0].Rotation);
        }

        private static void BuildPlan(FloatingPartPlacement at)
        {
            var first = Items[0];
            Quaternion turn = at.Rotation * Quaternion.Inverse(first.Rotation);
            Plan.Clear();
            PlanValid.Clear();
            _blocked = null;
            float reach = 0f;
            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                var placement = at;
                if (i > 0)
                {
                    Quaternion rotation = turn * item.Rotation;
                    placement = new FloatingPartPlacement(at.Coord + turn * (item.Position - first.Position),
                        PartOrientationHelper.SnapAnyOrientation(rotation * Vector3.forward),
                        PartOrientationHelper.SnapAnyOrientation(rotation * Vector3.up));
                }
                Plan.Add(placement);
                PlanValid.Add(true);
                reach = Mathf.Max(reach, (placement.Coord - at.Coord).magnitude + PartGeometry.Radius(item.Settings));
            }

            var ship = Ship(_targetShip);
            if (ship == null)
                return;
            var near = new List<PartBox>();
            foreach (var part in ship.StatefulParts)
            {
                if (part.Settings != null && (part.LocalPosition - at.Coord).magnitude <= reach + PartGeometry.Radius(part.Settings))
                    near.Add(PartGeometry.Box(part.Settings, part.LocalPosition, part.LocalRotation));
            }
            for (int i = 0; i < Items.Count; i++)
            {
                var box = PartGeometry.Box(Items[i].Settings, Plan[i].Coord, Plan[i].Rotation);
                if (!near.Any(other => box.Overlaps(other)))
                    continue;
                PlanValid[i] = false;
                if (_blocked == null)
                    _blocked = Items[i].Settings.fullLabel + " touche une pièce";
            }
            if (_blocked == null)
                _blocked = GameRefs.IsServer ? BuildCost.Shortfall(Items.Select(i => i.Settings)) : "réservé à l'hôte";
        }

        private static void DrawSelection()
        {
            if (!SelectGhosts.Begin(_sourceShip))
                return;
            foreach (var item in Items)
                SelectGhosts.Part(item.Settings, item.Placement, true, 1.04f);
            DrawCables(SelectGhosts, i => Items[i].Position, i => Items[i].Rotation);
            SelectGhosts.End();
        }

        private static void DrawPaste()
        {
            if (!PasteGhosts.Begin(_targetShip))
                return;
            // The build preview already shows the first part.
            for (int i = 1; i < Items.Count; i++)
                PasteGhosts.Part(Items[i].Settings, Plan[i], PlanValid[i]);
            DrawCables(PasteGhosts, i => Plan[i].Coord, i => Plan[i].Rotation);
            PasteGhosts.End();
        }

        private static void DrawCables(GhostSet ghosts, System.Func<int, Vector3> position, System.Func<int, Quaternion> rotation)
        {
            var grid = ghosts.Placements;
            if (grid == null)
                return;
            foreach (var cable in Cables)
            {
                Vector3 a = position(cable.A) + rotation(cable.A) * PartGeometry.SocketLocal(Items[cable.A].Settings, cable.SocketA);
                Vector3 b = position(cable.B) + rotation(cable.B) * PartGeometry.SocketLocal(Items[cable.B].Settings, cable.SocketB);
                ghosts.Line(new[] { World(grid, a), World(grid, b) }, CableColor(cable.Type), 0.015f);
            }
        }

        private static Vector3 World(IShipPartPlacements grid, Vector3 local)
        {
            return grid.ToWorldPos(new FloatingPartPlacement(local, PartOrientation.Forward, PartOrientation.Up));
        }

        private static Color CableColor(CableType type)
        {
            switch ((int)type)
            {
                case 1: return new Color(0.24f, 0.55f, 0.95f);
                case 2: return new Color(1f, 0.85f, 0.2f);
                default: return new Color(0.95f, 0.55f, 0.55f);
            }
        }
    }
}
