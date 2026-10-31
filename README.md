# Build Zoop — StellarDrive mod

Satisfactory-style **zoop** for **StellarDrive (Demo)**, plus everything Build Plus did. A MelonLoader mod built on SDModKit, packaged for [Stellar Mod Manager](https://github.com/jollyname/StellarModManager). It **replaces Build Plus**: remove `BuildPlus.dll`, both patch the build tool.

## Screenshot gallery

### Area — preview

The blue hologram previews a 7 × 6 rectangle in the plane of the selected floor panel.

![Blue holographic preview of a rectangular area of 42 floor panels](Mods/images/02-zone-apercu.png)

### Area — built result

The same rectangle after placing all 42 floor panels with one zoop.

![Completed rectangular area containing 42 floor panels](Mods/images/03-zone-resultat.png)

### Diagonal area with edges — preview

The area follows the grid at 45 degrees. Floor corners fill the stepped perimeter to make straight edges.

![Blue preview of a diagonal floor area with triangular edge pieces](Mods/images/04-diagonale-bords-apercu.png)

### Diagonal area with edges — built result

A completed diagonal surface made from 57 full and triangular floor pieces.

![Completed diagonal area with straight borders filled by triangular floor corners](Mods/images/05-diagonale-bords-resultat.png)

### Area on slopes — preview

Area mode follows the slope plane, previewing a continuous surface of 30 slope panels.

![Blue holographic preview of a continuous inclined area of 30 slope panels](Mods/images/06-slopes-zone-apercu.png)

### Area on slopes — built result

The completed inclined surface, placed with one area zoop.

![Completed inclined surface built from 30 slope panels](Mods/images/07-slopes-zone-resultat.png)

### Selection wheel (hold V)

Hold **V**, point at a mode, then release to select it. A short tap cycles to the next mode.

![Build Zoop selection wheel with seven building modes](Mods/images/01-roue-selection.png)

Screenshots captured in a prepared single-player sandbox with free build, a fixed camera, and physics held still for framing. They illustrate the native zoop shapes and previews.

## Installation

Use Stellar Mod Manager, or install manually:

1. Install MelonLoader 0.7.x and SDModKit for StellarDrive Demo.
2. Extract `StellarDrive-BuildZoop.zip` into the game folder. The DLL belongs in `Mods/BuildZoop.dll`.
3. Remove `BuildPlus.dll` if installed, then start the game.

For multiplayer, install Build Zoop and SDModKit on every player's game.

## Zoop

Hold **Left Shift** with the build tool, aim, **left click**. The first part is the spot under the cursor when Shift goes down; the rest follows your aim, even into empty space, and grows from the first part so every panel touches the previous one.

**Hold V** to open the mode wheel: move the mouse towards a mode and release (right click cancels; the camera does not turn while it is open). A short tap on V still goes to the next mode.

| Mode | What it lays |
| --- | --- |
| **Horizontale** | A flat row along the part's own directions: floors and walls along the axes, **pivoted (diagonal) walls along their diagonal**, slopes along their ramp. Aiming up or down never bends it. |
| **Verticale** | A column straight up or down (stacked walls, floors, pillars). |
| **Diagonale** | A row in any of the 26 directions: **stairs** of floors, diagonal rows of walls, 3D diagonals. |
| **Pyramide** | With a slope corner (lower or upper): one big flat triangle in the corners' sloped plane, lower and upper corners tiling it, one piece narrower on every row. Aim up to narrow it upwards (lower corners along the base), down to narrow it downwards (upper corners along the top). |
| **Zone** (area) | A rectangle in the plane of the first part: floors, walls, **whole diagonal wall surfaces**, slope roofs. Turn off "Zone à plat" for the old 3D box. |
| **Zone diagonale** | The same rectangle turned 45 degrees, from the first part (one corner) to the aim (the opposite one). |
| **Zone diag. + bords** | For flat floors: the 45 degree area with its stepped edges filled with floor corners (flat triangles), so the edges are straight. Other parts behave like Zone diagonale. |

- Slope corners zoop like slopes: rows along the axes, and up their ramps.
- Spots already taken and parts you cannot pay for show in **red** and are skipped (the server does not check either, so nothing gets overwritten). The key tip shows how many parts will be placed and how many lack materials.
- Works with floating parts (step = part size) and with the **recycler** to remove a line, diagonal or area of panels (or a box of floating parts).
- **Backspace** undoes the last zoop (removes what it placed, or puts back what it removed). Up to 30 steps.
- While zooping, the build tool's own preview is hidden: the zoop ghosts show what will be placed.
- Options in the mod menu: hold or toggle the key, flat or 3D areas, max parts per zoop (2000 by default, up to 10000). Big zoops are sent over a few seconds (120 parts per frame) so the server keeps up; the key tip shows what is left to send.

## Two panels in one voxel

Pivoted walls, slopes and slope corners all live in the single "interior" slot of a voxel, so the game refuses a slope corner next to a diagonal wall (although it takes one next to a straight wall), under a slope, or above another corner. Each of these parts is one flat panel cutting its voxel, and two panels can share it as long as they do not cross inside it. Build Zoop adds a second interior slot for such pairs:

- a slope corner along a diagonal wall, on either side;
- a lower slope corner under a slope, an upper one above it;
- an upper slope corner above a lower one.

Placement targeting uses the game's original snapping. Build Zoop does not redirect the preview into occupied voxels or change which positions the native snap detectors consider available. Zoop can still place compatible panels into the second slot, and existing second-slot parts remain supported by loading, saving, recycling, painting and pick.

- These second parts are saved in `buildzoop-hull.txt` next to `world.json`, not in `world.json` itself: without the mod the world still loads, those parts are just missing.
- In multiplayer every player needs the mod.

## From Build Plus

- **Middle click pick**: middle click a part to select it in the build tool, with its rotation and its settings (constant value, gate operation, channel, beacon name, lever height).
- **Placement lock**: **K** freezes the preview, arrows and Page Up / Page Down nudge it one cell (or a quarter cell for floating parts), relative to where you look.
- **Rotation lock**: **L**.
- **Circuit copy** (host): Shift + middle click picks floating parts and the cables between them; release Shift and the paste follows the build preview; undo from the menu.

Every key can be rebound in the SDModKit keybinds page.

## Build

```sh
./build.sh          # dist/StellarDrive-BuildZoop.zip (mod.json, Thumbnail.png, Mods/BuildZoop.dll)
./dev.sh            # DEV build: deploys, starts the game on the BZ_Test world
./cmd.sh "zoop 7 1 3 0 0 Floor Up Forward Line 8 -0.5 0"   # dev commands, see src/Dev/DevHarness.cs
```

On Windows, run `./build.ps1` to build the same release ZIP, including the gallery.

The project references the game, MelonLoader and SDModKit from the default Steam install (`-p:GamePath=...` to change it).

## License

[MIT](LICENSE) — Copyright (c) 2026 tidic84.
