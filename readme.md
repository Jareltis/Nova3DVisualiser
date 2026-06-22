# Nova3DVisualiser

A real-time 3D engine that renders to the terminal as ASCII art, written in C# / .NET 8. It is a CPU raytracer: every frame it traces rays into the scene, maps brightness to a character gradient, and (on truecolor terminals) tints each cell with 24-bit RGB, drawing the result directly in the console.

It ships with a multiplayer sample app that has a full **in-scene editor**, a **world system** (each scene is one JSON file you can create, save, and reload), a **rich colored-lighting** pipeline, and basic TCP networking with chat.

---

## Features

### Rendering
- CPU raytracing rendered to ASCII via a brightness gradient.
- **Truecolor output**: each cell carries a 24-bit RGB color (`Rgb24`) emitted as ANSI escape codes, so the picture is no longer limited to the 16 console colors. On terminals without truecolor the nearest console color is used.
- Floating-point lighting pipeline with per-channel **Reinhard tone mapping**, an ambient term, and an adjustable exposure.
- Smooth (per-vertex-normal) shading, with a Blender-friendly OBJ loader (n-gon fan triangulation, negative/relative indices, geometric-normal fallback).
- **BVH acceleration** for high-poly meshes, built once in local space and traversed with the ray transformed per frame, so it works even with animated/spinning models.
- Per-object bounding-sphere culling for both primary and shadow rays.
- Optional shadows (toggle per world).
- Adjustable render detail (1–4): trade resolution for speed live with one key.

### Lighting
Nova has a full multi-light system. Every light is shadow-tested **independently** and contributes additively, so a surface shadowed from one light is still lit by another (no single dominant shadow). Light kinds:

- **Point** — omnidirectional, with inverse-square falloff.
- **Directional** — a parallel "sun" with a constant mild attenuation.
- **Spot** — a cone aimed along a direction, with extra shaping (see below).
- **Area** — a square emitter sampled on a grid for area falloff + soft shadows.

Per-light extras:

- **Colored lighting** — each light emits an RGB color (taken from its `ConsoleColor`). A `SurfaceTint` term lets a colored light read on any surface, not only same-colored ones.
- **Spin** — sweeps a light's direction over time (rad/s) for animated lighting.
- **Multi-directional spot beams** — a spot can throw **1–8 cones** at once (`Beams`), fanned evenly around its aim; the spot lights whichever beam reaches a point.
- **Spot cone shape** — the cone cross-section can be **Circle**, **Square**, or **Triangle**.

### World system & editor
- A **world** is a single `worlds/<name>.json` file that owns the whole scene: graphics toggles, the platform, and every object's full transform (position/rotation/scale/color/anchor and, for lights, kind/direction/cone/beams/shape/etc.).
- `models/` is a **pure mesh library** — just `<name>.obj` files. The world references a mesh by name and supplies its placement.
- An **in-scene editor** (toggled with `Tab`) lets you spawn, move, rotate, scale, recolor, and delete objects live, then save back to JSON.
- Built-in spawn types: `cube`, `sphere`, `cylinder`, `cone`, `pyramid`, `light`, plus every mesh in `models/`.
- **Platform shapes**: square, rectangle, or circle, with configurable size/color.

### Networking
- Basic TCP server/client with a shared scene and in-app chat.
- The **server is the world authority**: it sends the full world to joining clients, then streams live edit deltas. Clients can fly around and inspect, but only the authority mutates the shared world.

### Other
- Timestamped file logging.
- Frame pacing (FPS cap), high-resolution delta time, and a quit key.

---

## Requirements
- .NET 8 SDK.
- Windows (keyboard input uses the Win32 API, so the app is currently Windows-only).
- A terminal that supports 24-bit ANSI color for the best output (Windows Terminal is recommended).

## Build & Run
```bash
dotnet build -c Release
```
Set `SampleGame` as the startup project in Visual Studio, or run it directly:
```bash
dotnet run --project SampleGame -c Release
```

### Launch setup (the menus before rendering)
A small Terminal.Gui wizard runs before the render loop:
1. **Session mode** — local solo, or online.
2. **Network role** (online only) — host a **Server** or join as a **Client**.
3. **World** — **Create** a new world or **Load** a saved one (`worlds/*.json`).
   - **Create** lets you set the world name, toggles (**Shadows**, **BVH acceleration**, **Extra fixed light**, **Disable camera light**, **Include platform**), the **platform shape** (Square / Rectangle / Circle) and its size/width/depth.
4. **Network** (online only) — listen port (server) or server IP + port (client).

A client that joins a server downloads the host's world automatically.

---

## Controls

Keys are grouped by mode. The camera (flight) controls work at all times; the editor controls only apply while the editor is open (`Tab`); chat captures all typing while open (`T`).

### Camera / flight (always active)
| Key | Action |
| --- | --- |
| **W** / **S** | Move forward / backward (relative to where you look) |
| **A** / **D** | Strafe left / right |
| **Space** / **C** | Move up / down |
| **← / →** | Look left / right (yaw) |
| **↑ / ↓** | Look up / down (pitch) |
| **Q** / **E** | Roll camera left / right |
| **R** | Reset roll to level |
| **Z** / **X** | Zoom in / out (FOV narrower / wider, 20°–120°) |
| **V** | Reset FOV to default |
| **=** / **-** | Increase / decrease your camera light power |
| **P** | Cycle render detail 1 → 2 → 3 → 4 → 1 (lower = faster) |
| **T** | Open chat (online sessions) |
| **Esc** | Quit the app |

### Editor (press **Tab** to toggle the editor on/off)
The editor shows a center crosshair and a properties panel for the selected object. Flight controls still work while editing.

| Key | Action |
| --- | --- |
| **Tab** | Toggle the editor |
| **G** | Cycle the spawn type (cube / sphere / cylinder / cone / pyramid / light / any `models/` mesh) |
| **Enter** | Spawn the current type in front of the camera *(authority only)* |
| **F** | Aim-select: pick the object under the crosshair |
| **[** / **]** | Select previous / next object |
| **,** / **.** | Move the field cursor up / down in the properties panel |
| **N** / **M** | Decrease / increase the value of the highlighted field *(authority only)* |
| **I** / **K** | Move the selected object along +Z / −Z *(authority only)* |
| **J** / **L** | Move the selected object along −X / +X *(authority only)* |
| **U** / **O** | Move the selected object up / down (+Y / −Y) *(authority only)* |
| **Delete** | Delete the selected object *(authority only)* |
| **F5** | Save the world back to `worlds/<name>.json` *(authority only)* |

> In an online session, only the **authority** (the server, or a local solo session) can spawn/move/edit/delete/save. A connected client can open the editor to **select and inspect** objects, but its changes never affect the shared world.

### Chat (press **T** to open, online only)
| Key | Action |
| --- | --- |
| *type* | Enter your message |
| **Enter** | Send |
| **Backspace** | Delete a character |
| **Esc** | Cancel without sending |

### The properties panel (edited with `,` `.` `N` `M`)
The list of editable fields depends on the selected object's type:

- **Mesh / cube / cylinder / cone / pyramid** — Pos X/Y/Z, Rot X/Y/Z, Scale, Spin (auto-rotate speed), Color.
- **Sphere** — Pos X/Y/Z, Radius, Color.
- **Light** — Pos X/Y/Z, Power, Color, **Kind**, then per kind:
  - *Point*: nothing extra.
  - *Directional*: Dir X/Y/Z, Spin.
  - *Spot*: Dir X/Y/Z, Cone (half-angle), **Beams** (1–8 fanned cones), **Shape** (Circle / Square / Triangle), Spin.
  - *Area*: Dir X/Y/Z, Size (half-extent), Spin.

`Color` cycles through the 16 named console colors; for a light it also sets the color it emits.

---

## Adding models
Drop a mesh into `SampleGame/models/` as `<name>.obj` (export with vertex normals for smooth shading; Blender does this by default). Models are a pure geometry library — there is **no per-model JSON** anymore; placement (position, rotation, scale, color, anchor, spin) lives in the world.

To place a model in a scene, either:
- Spawn it live in the editor (`G` to find it in the spawn list, then `Enter`), arrange it, and `F5` to save; or
- Add an object entry to the world's `worlds/<name>.json` by hand.

A world object looks like this:
```json
{
  "id": 0,
  "type": "mesh",
  "mesh": "monkey",
  "position": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "rotation": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "scale": 1.0,
  "color": "Yellow",
  "anchor": "bottom",
  "rotateSpeed": 1.0
}
```
- `type` — `mesh`, `cube`, `sphere`, `cylinder`, `cone`, `pyramid`, or `light`.
- `mesh` — the `models/` file name (without `.obj`), for `type: "mesh"`.
- `position` — world position in units (Y is the vertical axis).
- `rotation` — initial rotation in radians.
- `scale` — uniform scale.
- `color` — a .NET `ConsoleColor` name (e.g. `Red`, `Cyan`, `Yellow`).
- `anchor` — how the mesh sits at its position: `bottom` (base on the floor, default), `center` (geometric center), or `origin` (raw OBJ origin).
- `rotateSpeed` — auto-spin around the vertical axis, in radians per second (0 = static).

A `light` object additionally uses: `power`, `lightKind` (`point`/`directional`/`spot`/`area`), `direction`, `coneAngle`, `beamCount`, `coneShape` (`circle`/`square`/`triangle`), `lightSize`, and `lightSpin`.

Edit a model or a world JSON and just re-run — no rebuild required.

---

## Project structure
- `Nova3DVisualiser/` — the engine (library): vector math, shapes, raytracing, the colored-lighting pipeline, truecolor screen output, BVH, OBJ/mesh loading, networking, logging. It is dependency-free.
- `SampleGame/` — the demo application: the setup wizard, the world system (`worlds/`), the in-scene editor, networking host, and the `models/` mesh library.

---

## Credits
Nova3DVisualiser is a modified and extended version of **Neo3dEngine** by **Ivan Sobolev** (https://github.com/IvanSobolev/Neo3dEngine), forked at version **v0.1.1**.
- Original engine © Ivan Sobolev, licensed under GPL-3.0.
- Modifications and new features © Jareltis, 2026.

Major changes from the original include: the renamed engine; a rewritten floating-point, **colored** lighting pipeline with per-channel tone mapping and **24-bit truecolor** output; a full multi-light system (point / directional / spot / area, colored emission, light spin, multi-beam spots, and circle/square/triangle cone shapes); smooth shading and a Blender-robust OBJ loader; a JSON **world system** with an **in-scene editor** and live network sync; folder-based mesh loading with anchoring; BVH acceleration; bounding-sphere culling for primary and shadow rays; a shadow toggle; timestamped logging; and main-loop improvements (quit key, FPS cap, high-resolution delta time).

## License
This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**. Because it is derived from Neo3dEngine (GPL-3.0), Nova3DVisualiser is and must remain GPL-3.0. See the [LICENSE](LICENSE) file for the full text.
