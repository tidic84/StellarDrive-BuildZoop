using System;
using System.Reflection;
using BuildZoop.Zoop;
using HarmonyLib;
using SDModKit;
using SDModKit.Api;
using SDModKit.Game;
using SDModKit.Input;
using SDModKit.Patching;
using SDModKit.UI;
using Tools.Build;
using Tools.Recycler;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BuildZoop
{
    public sealed class BuildZoopMod : SDMod
    {
        private static readonly Type HintsType = AccessTools.TypeByName("Managers.Client.Services.HintsActivator");
        private static readonly MethodInfo HideHintInstant = HintsType == null ? null : AccessTools.Method(HintsType, "DisableHintInstant");
        private static readonly FieldInfo ShowingThrustHint = HintsType == null ? null : AccessTools.Field(HintsType, "_isShowingThrustHint");

        public override string Id => "buildzoop";

        public override string Name => "Build Zoop";

        public override void OnRegistered()
        {
            Settings.Bind(Config);
            Settings.ZoopKey = Keybinds.Register(Config, "Keys", "Zoop", Key.LeftShift, "Zoop (maintenir)");
            Settings.ModeKey = Keybinds.Register(Config, "Keys", "ZoopMode", Key.V, "Mode de zoop");
            Settings.UndoKey = Keybinds.Register(Config, "Keys", "Undo", Key.Backspace, "Annuler le dernier zoop");
            Settings.PickKey = Keybinds.RegisterMouse(Config, "Keys", "PickPart", MouseBind.Middle, "Copier la pièce visée");
            Settings.RotationLockKey = Keybinds.Register(Config, "Keys", "RotationLock", Key.L, "Bloquer la rotation");
            PlacementLock.LockKey = Keybinds.Register(Config, "Keys", "PlacementLock", Key.K, "Bloquer la position");
            PlacementLock.Forward = Keybinds.Register(Config, "Keys", "NudgeForward", Key.UpArrow, "Décaler en avant");
            PlacementLock.Back = Keybinds.Register(Config, "Keys", "NudgeBack", Key.DownArrow, "Décaler en arrière");
            PlacementLock.Left = Keybinds.Register(Config, "Keys", "NudgeLeft", Key.LeftArrow, "Décaler à gauche");
            PlacementLock.Right = Keybinds.Register(Config, "Keys", "NudgeRight", Key.RightArrow, "Décaler à droite");
            PlacementLock.Up = Keybinds.Register(Config, "Keys", "NudgeUp", Key.PageUp, "Décaler vers le haut");
            PlacementLock.Down = Keybinds.Register(Config, "Keys", "NudgeDown", Key.PageDown, "Décaler vers le bas");

            HarmonyLib.Harmony harmony = ModPatches.For(Plugin.Guid);
            ApplyPatches(harmony);
            PlacementLock.Apply(harmony, Log);
            Pick.Apply(harmony, Log);
            Pairing.SharedVoxel.Apply(harmony, Log);
            AddPage("Build", DrawPage).Available = () => GameRefs.InWorld;
        }

        public override void OnWorldExit()
        {
            PlacementLock.Unlock();
            ZoopBuilder.Reset();
            CircuitCopy.Reset();
        }

        public override void OnUpdate()
        {
            if (ModKit.Menu != null && ModKit.Menu.IsOpen)
            {
                ZoopBuilder.Cancel();
                return;
            }
            if (!GameRefs.InWorld)
                return;
            ZoopBuilder.Pump();

            BuildToolLocalClient build = ToolRefs.Build;
            bool buildActive = ToolRefs.BuildActive;
            PlacementLock.Update(build);
            if (PlacementLock.Locked && !LocalPlayer.GameMenuOpen)
                LockTips();

            if (Settings.RotationLockKey.Pressed && !LocalPlayer.GameMenuOpen)
                Settings.RotationLocked.Value = !Settings.RotationLocked.Value;

            bool zoopHeld = Settings.ZoopKey.Held;
            if (Settings.PickKey.Pressed && zoopHeld && buildActive)
                CircuitCopy.Click();
            else if (Settings.PickEnabled.Value && Settings.PickKey.Pressed)
                Pick.Click();
            Pick.Update();

            if (CircuitCopy.HasSelection)
                ZoopBuilder.Cancel();
            else
                ZoopBuilder.Update();
            CircuitCopy.Update(build, zoopHeld && buildActive);
        }

        private static void LockTips()
        {
            KeyTips.Mode("Position bloquée");
            KeyTips.HideVanilla("rotate");
            var p = PlacementLock.Forward.Display + PlacementLock.Back.Display + PlacementLock.Left.Display + PlacementLock.Right.Display;
            if (p == "UpArrowDownArrowLeftArrowRightArrow")
            {
                KeyTips.Add("Flèches", "Décaler");
            }
            else
            {
                KeyTips.Add(PlacementLock.Forward, "Avant");
                KeyTips.Add(PlacementLock.Back, "Arrière");
                KeyTips.Add(PlacementLock.Left, "Gauche");
                KeyTips.Add(PlacementLock.Right, "Droite");
            }
            KeyTips.Add(PlacementLock.Up, "Haut");
            KeyTips.Add(PlacementLock.Down, "Bas");
            KeyTips.Add(PlacementLock.LockKey, "Débloquer");
        }

        // ---------------------------------------------------------------- patches

        private void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            var place = AccessTools.Method(typeof(BuildToolLocalClient), "PrimaryActionBegin");
            if (place != null)
                harmony.Patch(place, new HarmonyMethod(typeof(BuildZoopMod), nameof(BeforePlace)));
            else
                Log.Warn("BuildToolLocalClient.PrimaryActionBegin not found, zoop disabled");

            var recycle = AccessTools.Method(typeof(RecyclerTool), "PrimaryActionBegin");
            if (recycle != null)
                harmony.Patch(recycle, new HarmonyMethod(typeof(BuildZoopMod), nameof(BeforeRecycle)));

            // The build hints keep the thrust hint on screen while piloting; hide it once it has been shown.
            var hints = HintsType == null ? null : AccessTools.Method(HintsType, "UpdateTips");
            if (hints != null && HideHintInstant != null && ShowingThrustHint != null)
                harmony.Patch(hints, postfix: new HarmonyMethod(typeof(BuildZoopMod), nameof(AfterUpdateHints)));
        }

        public static bool BeforePlace()
        {
            if (ZoopBuilder.TryCommit())
                return false;
            return !CircuitCopy.TryPaste();
        }

        public static bool BeforeRecycle()
        {
            return !ZoopBuilder.TryCommit();
        }

        public static void AfterUpdateHints(object __instance)
        {
            if (LocalPlayer.IsPiloting && !(bool)ShowingThrustHint.GetValue(__instance))
                HideHintInstant.Invoke(__instance, null);
        }

        // ---------------------------------------------------------------- menu page

        private void DrawPage()
        {
            ModUi.BeginSection("Zoop");
            ModUi.Row("Activé", () => Settings.ZoopEnabled.Value = ModUi.Toggle(Settings.ZoopEnabled.Value, string.Empty));
            ModUi.Row("Mode", () =>
            {
                foreach (ZoopMode mode in Settings.ModeOrder)
                {
                    bool on = Settings.CurrentMode == mode;
                    if (ModUi.Button((on ? "> " : string.Empty) + Settings.ModeName(mode), 100f))
                        Settings.CurrentMode = mode;
                }
            });
            ModUi.Row("Touches", () => GUILayout.Label(Settings.ZoopKey.Display + " maintenu + clic gauche, " + Settings.ModeKey.Display + " change de mode", Theme.RowLabel));
            ModUi.Row("Maintenir", () =>
            {
                Settings.HoldToZoop.Value = ModUi.Toggle(Settings.HoldToZoop.Value, string.Empty);
                GUILayout.Label(Settings.HoldToZoop.Value ? "maintenir la touche" : "appuyer une fois pour commencer", Theme.Status);
            });
            ModUi.Row("Zone à plat", () =>
            {
                Settings.FlatArea.Value = ModUi.Toggle(Settings.FlatArea.Value, string.Empty);
                GUILayout.Label(Settings.FlatArea.Value ? "dans le plan de la 1re pièce" : "boîte 3D", Theme.Status);
            });
            ModUi.Row("Max pièces", () => Settings.Limit.Value = ModUi.Int(Settings.Limit.Value, 1, Settings.MaxLimit, 90f));
            ModUi.Row("État", () =>
            {
                if (ZoopBuilder.Sending > 0)
                    GUILayout.Label("envoi : " + ZoopBuilder.Sending + " restantes", Theme.GoodText);
                else if (ZoopBuilder.Active)
                    GUILayout.Label(ZoopBuilder.Count + " prévues", Theme.GoodText);
                else
                    GUILayout.Label(ZoopBuilder.LastAction ?? ZoopBuilder.Status ?? "en attente", Theme.Status);
                if (ZoopBuilder.CanUndo && ModUi.Button("Annuler", 80f))
                    ZoopBuilder.Undo();
            });
            ModUi.Note("Horizontale : rangée à plat (murs diagonaux le long de leur diagonale, pentes le long de leur rampe). "
                + "Verticale : empilement vers le haut ou le bas. Diagonale : les 26 directions (escaliers, murs en biais). "
                + "Zone : rectangle dans le plan de la pièce. "
                + "Pyramide (avec un coin de pente) : grand triangle de coins de pente bas et haut, une pièce de moins à chaque rangée, vers le haut ou vers le bas.");
            ModUi.EndSection();

            ModUi.BeginSection("Copie au clic molette");
            ModUi.Row("Activé", () => Settings.PickEnabled.Value = ModUi.Toggle(Settings.PickEnabled.Value, string.Empty));
            ModUi.Row("Prendre l'outil", () => Settings.PickEquipsBuildTool.Value = ModUi.Toggle(Settings.PickEquipsBuildTool.Value, string.Empty));
            ModUi.Row("Copier réglages", () => Settings.CopySettings.Value = ModUi.Toggle(Settings.CopySettings.Value, string.Empty));
            ModUi.Row("Copier rotation", () => Settings.CopyRotation.Value = ModUi.Toggle(Settings.CopyRotation.Value, string.Empty));
            ModUi.Row("Bouton", () => GUILayout.Label(Settings.PickKey.Display, Theme.RowLabel));
            ModUi.Row("Dernière", () => GUILayout.Label(Pick.LastResult ?? "rien", Theme.Status));
            ModUi.Row("Réglages", () => GUILayout.Label(Pick.Copied ?? "aucun", Theme.Status));
            ModUi.EndSection();

            ModUi.BeginSection("Copie de circuit");
            ModUi.Row("Sélection", () => GUILayout.Label(Settings.ZoopKey.Display + " + " + Settings.PickKey.Display, Theme.RowLabel));
            ModUi.Row("Copié", () =>
            {
                GUILayout.Label(CircuitCopy.HasSelection ? CircuitCopy.Describe() : "rien", Theme.Status);
                if (CircuitCopy.HasSelection && ModUi.Button("Effacer", 80f))
                    CircuitCopy.Clear();
            });
            ModUi.Row("Dernier collage", () =>
            {
                GUILayout.Label(CircuitCopy.Status ?? "aucun", Theme.Status);
                if (CircuitCopy.CanUndo && ModUi.Button("Annuler", 80f))
                    CircuitCopy.Undo();
            });
            ModUi.Row("Hôte", () => GUILayout.Label(GameRefs.IsServer ? "vous hébergez, le collage marche" : "réservé à l'hôte",
                GameRefs.IsServer ? Theme.GoodText : Theme.Status));
            ModUi.EndSection();

            ModUi.BeginSection("Blocage de position");
            ModUi.Row("État", () => GUILayout.Label(PlacementLock.Locked ? "bloquée" : "libre", PlacementLock.Locked ? Theme.GoodText : Theme.Status));
            ModUi.Row("Touche", () => GUILayout.Label(PlacementLock.LockKey.Display, Theme.RowLabel));
            ModUi.Row("Décaler", () => GUILayout.Label(PlacementLock.Forward.Display + " " + PlacementLock.Back.Display + " "
                + PlacementLock.Left.Display + " " + PlacementLock.Right.Display, Theme.RowLabel));
            ModUi.Row("Haut / bas", () => GUILayout.Label(PlacementLock.Up.Display + " " + PlacementLock.Down.Display, Theme.RowLabel));
            ModUi.EndSection();

            ModUi.BeginSection("Rotation");
            ModUi.Row("Bloquer", () =>
            {
                Settings.RotationLocked.Value = ModUi.Toggle(Settings.RotationLocked.Value, string.Empty);
                GUILayout.Label(Settings.RotationLockKey.Display + " en jeu", Theme.Status);
            });
            ModUi.EndSection();
        }
    }
}
