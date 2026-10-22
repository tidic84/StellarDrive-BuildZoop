# Build Zoop — StellarDrive mod

Satisfactory-style **zoop** for **StellarDrive (Demo)**, plus everything Build Plus did. A MelonLoader mod built on SDModKit, packaged for [Stellar Mod Manager](https://github.com/jollyname/StellarModManager). It **replaces Build Plus**: remove `BuildPlus.dll`, both patch the build tool.

![Diagonal walls and a slope ramp laid with one zoop each](docs/diagonal_walls.png)

## Zoop

Hold **Left Shift** with the build tool, aim, **left click**. The first part is the spot under the cursor when Shift goes down; the rest follows your aim, even into empty space, and grows from the first part so every panel touches the previous one.

**V** cycles the mode:

| Mode | What it lays |
| --- | --- |
| **Horizontale** | A flat row along the part's own directions: floors and walls along the axes, **pivoted (diagonal) walls along their diagonal**, slopes along their ramp. Aiming up or down never bends it. |
| **Verticale** | A column straight up or down (stacked walls, floors, pillars). |
| **Diagonale** | A row in any of the 26 directions: **stairs** of floors, diagonal rows of walls, 3D diagonals. |
| **Zone** (area) | A rectangle in the plane of the first part: floors, walls, **whole diagonal wall surfaces**, slope roofs. Turn off "Zone à plat" for the old 3D box. |

- Spots already taken and parts you cannot pay for show in **red** and are skipped (the server does not check either, so nothing gets overwritten). The key tip shows how many parts will be placed and how many lack materials.
- Works with floating parts (step = part size) and with the **recycler** to remove a line, diagonal or area of panels (or a box of floating parts).
- **Backspace** undoes the last zoop (removes what it placed, or puts back what it removed). Up to 30 steps.
- Options in the mod menu: hold or toggle the key, flat or 3D areas, max parts per zoop (2000 by default, up to 10000). Big zoops are sent over a few seconds (120 parts per frame) so the server keeps up; the key tip shows what is left to send.

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

The project references the game, MelonLoader and SDModKit from the default Steam install (`-p:GamePath=...` to change it).
