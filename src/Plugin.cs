using MelonLoader;
using SDModKit;
using SDModKit.Api;
using SDModKit.Configuration;

[assembly: MelonInfo(typeof(BuildZoop.Plugin), "StellarDrive Build Zoop", "1.3.0", "tidic84")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]
[assembly: HarmonyDontPatchAll]
[assembly: StellarMod("buildzoop", "Build Zoop",
    Description = "Satisfactory-style zoop: lines, diagonals (diagonal walls, slopes, stairs) and areas, with undo. Also middle click pick, placement lock with nudging, rotation lock and circuit copy. Replaces Build Plus.")]

namespace BuildZoop
{
    public sealed class Plugin : MelonMod
    {
        public const string Guid = "stellardrive.buildzoop";

        public override void OnInitializeMelon()
        {
            foreach (var melon in RegisteredMelons)
            {
                if (melon.Info.Name == "StellarDrive Build Plus")
                    LoggerInstance.Warning("Build Plus is also installed: remove it, Build Zoop replaces it (both patch the build tool).");
            }
            ModKit.Register(new BuildZoopMod(), ConfigFile.For(Guid));
#if DEV
            Dev.DevHarness.Init(LoggerInstance);
#endif
        }

#if DEV
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Dev.DevHarness.SceneLoaded(buildIndex, sceneName);
        }

        public override void OnUpdate()
        {
            Dev.DevHarness.Update();
        }
#endif
    }
}
