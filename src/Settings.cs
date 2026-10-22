using SDModKit.Configuration;
using SDModKit.Input;

namespace BuildZoop
{
    public enum ZoopMode
    {
        Line,
        Diagonal,
        Area
    }

    public static class Settings
    {
        public static ConfigEntry<bool> ZoopEnabled;
        public static ConfigEntry<int> Mode;
        public static ConfigEntry<bool> HoldToZoop;
        public static ConfigEntry<bool> FlatArea;
        public static ConfigEntry<int> Limit;

        public static ConfigEntry<bool> PickEnabled;
        public static ConfigEntry<bool> PickEquipsBuildTool;
        public static ConfigEntry<bool> CopySettings;
        public static ConfigEntry<bool> CopyRotation;

        public static ConfigEntry<bool> RotationLocked;

        public static Keybind ZoopKey;
        public static Keybind ModeKey;
        public static Keybind UndoKey;
        public static Keybind PickKey;
        public static Keybind RotationLockKey;

        public static ZoopMode CurrentMode
        {
            get => (ZoopMode)(((Mode.Value % 3) + 3) % 3);
            set => Mode.Value = (int)value;
        }

        public static void Bind(ConfigFile config)
        {
            ZoopEnabled = config.Bind("Zoop", "Enabled", true, "Hold the zoop key with the build tool or the recycler to place or remove many parts at once.");
            Mode = config.Bind("Zoop", "Mode", 0, "0 = line, 1 = diagonal, 2 = area.");
            HoldToZoop = config.Bind("Zoop", "Hold", true, "Hold the key to zoop; off = press once to start, again to cancel.");
            FlatArea = config.Bind("Zoop", "FlatArea", true, "Areas stay in the plane of the first part (off = 3D box).");
            Limit = config.Bind("Zoop", "Limit", 400, "Most parts placed in one go.");

            PickEnabled = config.Bind("Pick", "Enabled", true, string.Empty);
            PickEquipsBuildTool = config.Bind("Pick", "EquipBuildTool", true, string.Empty);
            CopySettings = config.Bind("Pick", "CopySettings", true, string.Empty);
            CopyRotation = config.Bind("Pick", "CopyRotation", true, string.Empty);

            RotationLocked = config.Bind("Rotation", "Locked", false, string.Empty);
        }

        public static string ModeName(ZoopMode mode)
        {
            switch (mode)
            {
                case ZoopMode.Diagonal: return "Diagonale";
                case ZoopMode.Area: return "Zone";
                default: return "Ligne";
            }
        }
    }
}
