# Nova3DVisualiser

A real-time 3D engine that renders to the terminal as ASCII art, written in C# / .NET 10. It is a raytracer — CPU by default, with an optional GPU path (ILGPU / CUDA) at full parity: every frame it traces rays into the scene, maps brightness to a character gradient, and (on truecolor terminals) tints each cell with 24-bit RGB, drawing the result directly in the console.

It ships with a multiplayer sample app that has a full **in-scene editor** (with a reworked, mouse-and-keyboard HUD), a **world system** (each scene is one JSON file you can create, save, and reload), a **rich colored-lighting** pipeline, **PNG textures**, a **real rigid-body physics** layer (a walking first-person character plus believable gravity — angle-of-incidence, rolling, bouncing, friction, mass and 3D spin), a **multi-view camera system**, and basic TCP networking with chat. Its whole UI — including the setup wizard — runs on the engine's own console renderer (no external UI toolkit).

---

## Features

### Rendering
- **CPU or GPU renderer, chosen per world.** The default is the multithreaded CPU raytracer. A world can instead select the **GPU** renderer (NVIDIA, via [ILGPU](https://ilgpu.net/) → CUDA — also runs on any OpenCL device), which casts every primary, shadow and lighting ray on the graphics card. It is at **full feature parity** with the CPU path: front-to-back transparency, every light kind (point / directional / spot / area), spot beam fans, circle/square/triangle cone shapes, and area soft shadows — validated pixel-for-pixel by the `gputest` self-test. It uses the same **two-level BVH** acceleration as the CPU (per-mesh local-space BVH, built once and uploaded once; the kernel transforms each ray into the object's space and traverses a stackless flattened tree), so heavy meshes stay fast. If a world asks for GPU but no usable GPU is present, it transparently falls back to the CPU renderer. Pick it in the **Create world** dialog (**Renderer: CPU / GPU**).
- CPU raytracing rendered to ASCII via a brightness gradient.
- **Truecolor output**: each cell carries a 24-bit RGB color (`Rgb24`) emitted as ANSI escape codes, so the picture is no longer limited to the 16 console colors. On terminals without truecolor the nearest console color is used.
- **Two kinds of transparency**: every object has both **object transparency** — the color's **alpha** channel, which makes it see-through (overlapping transparent surfaces composite **front-to-back** via depth peeling, and a transparent object casts a correspondingly lighter shadow) — and **color paleness** (the **Pale** value), a *separate* knob that washes the surface's own color toward white while the object stays fully solid and casts a full shadow.
- **PNG textures + UV**: any object can wear a **PNG** image (8-bit RGB / RGBA), decoded in-app (no third-party image library). Textures map by per-corner UVs — generated procedurally for the primitives (cube / sphere / cylinder / cone / pyramid / ramp / flatpicture; the sphere uses an **equirectangular** map) and read from the `.obj` for imported meshes. Per object you can set the **tiling** (`TexScale`), **which face** wears it (`TexFace` — e.g. one of a cube's six sides), and the **filtering** — **nearest**, **bilinear**, or **mipmapped** (trilinear minification, so distant / grazing surfaces don't shimmer). Textures render **identically on CPU and GPU** (validated by `gputest`), and in multiplayer the PNG **bytes stream to peers**, so a client without the file still sees the texture.
- Floating-point lighting pipeline with per-channel **Reinhard tone mapping**, an ambient term, and an adjustable exposure.
- Smooth (per-vertex-normal) shading, with a Blender-friendly OBJ loader (n-gon fan triangulation, negative/relative indices, geometric-normal fallback).
- **BVH acceleration** for high-poly meshes, built once in local space and traversed with the ray transformed per frame, so it works even with animated/spinning models.
- Per-object bounding-sphere culling for both primary and shadow rays.
- Optional shadows (toggle per world).
- Adjustable render detail (1–4): trade resolution for speed live with one key.
- **Diff rendering**: each frame only the console cells that actually changed are rewritten (with ANSI cursor jumps), instead of repainting the whole screen. A still camera writes almost nothing, which removes the top-to-bottom "tearing" and raises the frame rate.

### Lighting
Nova has a full multi-light system. Every light is shadow-tested **independently** and contributes additively, so a surface shadowed from one light is still lit by another (no single dominant shadow). Shadows are **alpha-correct**: an opaque object casts a full shadow, while a transparent one casts a correspondingly lighter shadow (the shadow ray accumulates each transparent occluder's alpha). Light kinds:

- **Point** — omnidirectional, with inverse-square falloff.
- **Directional** — a parallel "sun" with a constant mild attenuation.
- **Spot** — a cone aimed along a direction, with extra shaping (see below).
- **Area** — a square emitter sampled on a grid for area falloff + soft shadows.

Per-light extras:

- **Colored lighting** — each light emits an RGB color (taken from its `ConsoleColor`). A `SurfaceTint` term lets a colored light read on any surface, not only same-colored ones.
- **Spin** — sweeps a light's direction over time (rad/s) for animated lighting.
- **Multi-directional spot beams** — a spot can throw **1–8 cones** at once (`Beams`), fanned evenly around its aim; the spot lights whichever beam reaches a point.
- **Spot cone shape** — the cone cross-section can be **Circle**, **Square**, or **Triangle**.

### Physics (optional, per world)
Nova has a **real rigid-body physics** layer — a unified **impulse-based constraint solver** (sequential impulse / PGS over a contact manifold, warm-started, with friction, rolling friction, restitution, split-impulse positional correction and sleeping islands). It gives believable gravity with angle-of-incidence, inertia, rolling, bouncing, spin, **solid stacks and emergent tumbling** — not a scripted fall. Two world-level master switches, both set when you create a world (and editable in the JSON):

- **Collision** *(on by default)* — the master switch for solid objects. The **player** (a camera bubble) is pushed out of the floor and any collider, so you can't walk or fly through them. Each mesh can use a fast **AABB** box collider or an **OBB** oriented box that hugs a tilted shape (box-box contacts use a full 3D Separating-Axis test). Each object has its own **Collide** flag; when the world switch is **off**, every object's Collide is forced off and locked.
- **Gravity** *(off by default)* — turns on the simulation:
  - The **player becomes a walking character**: gravity pulls the camera down, it stands on surfaces, **Space** jumps, and **F1** toggles a free-fly mode for building.
  - Any object can opt into **Gravity** — meshes, primitives, spheres, even a light or the platform fall and rest on whatever is beneath them.
  - A falling **ball collides with the real surface** it lands on — the actual triangles of a pyramid / ramp / mesh, **not its bounding box** — so it **rolls down a slope with real momentum** (faster on steeper inclines), coasts to a stop on the flat, and **bounces** by its restitution. A falling mesh likewise rests on the real surface under it.
  - **Boxes rest, slide or topple on the real surface — never flung.** A box dropped on a ramp settles **flat on the slope** and slides or tumbles down it; it is **not** ejected sideways (the box collides with the mesh's real triangles, not its bounding box). Whether it **slides or tumbles** depends on the **slope steepness and friction**: a shallow slope just slides, a steep face / a box off-balance **topples over its edge** and rolls edge-to-edge down, settling at the bottom — all **emergent** from the solver.
  - **Everything collides with everything, and boxes STACK.** Boxes and balls collide in every combination (box-box, ball-box, ball-ball), mass-weighted, so a **tower of boxes stands solid** (no jitter, no lean, no sink) and a **ball fired into it scatters the tower** — and the whole pile then **settles and sleeps** together.
  - **Rolling friction** — a rolling ball and a tumbling box **slow down and come to rest** instead of rolling forever, while still rolling **down** a slope (gravity beats the small rolling resistance). Tunable per object.
  - **Restitution (Bounce)** is set per-world (Create dialog) and per-object: `0` = dead stop, `1` = perfectly elastic. On a contact the two surfaces' bounciness **combine**, so a springy ball on a dead floor differs from one on a "trampoline" wall.
  - **Friction, mass and 3D rotation**: objects shed speed to friction (Coulomb + rolling), a heavier object is shoved less (per-object **Mass**), and an off-centre hit makes a body **tumble** — full 3D spin with a proper inertia tensor and drift-free quaternion orientation.
  - The simulation **substeps** internally, so it behaves the same whether the terminal renders at 60 FPS or only a few, with safety rails so nothing flies off.

Object physics is simulated **by the authority/solo** and each moved body's full state — position, orientation and velocity — is streamed to clients, which **dead-reckon and smoothly ease** toward it, so everyone sees objects **fall, roll and tumble** in sync (not frozen between updates); the player's own gravity runs locally for every peer, so nothing desyncs.

### World system & editor
- A **world** is a single `worlds/<name>.json` file that owns the whole scene: graphics toggles, the platform, and every object's full transform (position/rotation/scale/color/anchor and, for lights, kind/direction/cone/beams/shape/etc.).
- `models/` is a **pure mesh library** — just `<name>.obj` files. The world references a mesh by name and supplies its placement.
- An **in-scene editor** (toggled with `Tab`) lets you spawn, move, rotate, scale, recolor, and delete objects live, then save back to JSON.
- Built-in spawn types: `cube`, `sphere`, `cylinder`, `cone`, `pyramid`, `ramp` (a wedge with a sloped top — great for rolling), `flatpicture` (a two-sided vertical panel that displays a texture — a poster/billboard, non-colliding), `light`, plus every mesh in `models/`.
- **Platform shapes**: square, rectangle, or circle, with configurable size/color.
- **Live graphics toggles**: flip **shadows** (`F2`), **BVH acceleration** (`F3`), the **camera headlight** (`F4`), and the **floor platform** (`F6`) right in the scene — no need to recreate the world. On a server these changes sync to every client.

### Cameras, views & HUD
- **You are a body + a camera.** In first person the camera is at your eyes; **F7** cycles to **3rd-person** (behind + above) and **2nd-person** (in front, looking back at you so you see your own face). The external views pull the camera in past any wall in the way; other players always see your avatar.
- **Placeable cameras.** Spawn a **camera** object (a viewpoint): a **Fixed** camera is a static shot from where you placed it; a **Follow** camera swivels to track a target — your body by default, or any object by id. **F8** cycles the active view through your body and every placed camera — a local, per-peer choice, so clients can look through cameras too.
- **Split-screen.** **F9** splits the screen 2-way — your active view on the left, the next view in the F8 cycle on the right — two cameras rendered at once.
- **Three HUD modes.** **PLAY** (a minimal crosshair over full-screen 3D), **overlay-edit** (**Tab** — the editor panels over the 3D), and **docked-edit** (**`** — a Unity/Blender-style layout: the 3D in a centre viewport with a Toolbar on top, a Status bar on the bottom, a navigable **Hierarchy** on the left, and a grouped **Inspector** on the right). Chat is a contained, word-wrapped, scrollable box that never bleeds over the HUD. The whole HUD **reflows when you resize or font-zoom the terminal**.

### Networking
- A basic TCP server/client with a shared scene and in-app chat. **Multiple clients** can join one server, and each player sees the others as a small moving **avatar**.
- The **server is the world authority**: it sends the full world to joining clients — large meshes are split into **chunks** for reliable transfer — then streams live edit deltas, graphics-setting changes, and physics (object positions **and spin**). Clients can fly around and inspect, but only the authority mutates the shared world.

### Other
- Timestamped file logging.
- Frame pacing (FPS cap), high-resolution delta time, and a quit key.

---

## Requirements
- .NET 10 SDK.
- Windows (keyboard input uses the Win32 API, and the wizard's mouse support uses the Win32 console API, so the app is currently Windows-only).
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
A branded setup wizard runs before the render loop. It is drawn by the **engine's own console UI** (no external UI toolkit) and is driven by **keyboard and mouse**; it re-lays-out when you resize the terminal:
1. **Session mode** — local solo, or online.
2. **Network role** (online only) — host a **Server** or join as a **Client**.
3. **World** — **Create** a new world or **Load** a saved one (`worlds/*.json`).
   - **Create** lets you set the world name, toggles (**Shadows**, **BVH acceleration**, **Extra fixed light**, **Disable camera light**, **Include platform**), the **Renderer** (**CPU** / **GPU (NVIDIA)**), the **platform shape** (Square / Rectangle / Circle) and its size/width/depth, and the **physics** switches (**Gravity** + its strength + **Bounce** restitution 0–1, **Collision**).
4. **Network** (online only) — listen port (server) or server IP + port (client).

A client that joins a server downloads the host's world automatically.

> **Zoom:** the wizard reflows when you **resize the window** or use **keyboard font-zoom** (Ctrl+= / Ctrl+-). Ctrl+**scroll** font-zoom doesn't resize the wizard (it captures the mouse wheel for clicks) — the in-game 3D still reflows on Ctrl+scroll.

---

## Controls

Keys are grouped by mode. The camera (flight) controls work at all times; the editor controls only apply while the editor is open (`Tab`); chat captures all typing while open (`T`).

### Camera / flight (always active)
| Key | Action |
| --- | --- |
| **W** / **S** | Move forward / backward (relative to where you look) |
| **A** / **D** | Strafe left / right |
| **Space** / **C** | Fly up / down *(fly mode)* — in a **gravity world**, **Space** jumps and **C** is unused |
| **F1** | Toggle **fly** ↔ **walk**. In a gravity world the default is **walk** (gravity pulls you down, you stand on the floor/objects, can't pass through them); fly mode restores free up/down flight and passes through objects |
| **F7** | Cycle the body view **1st-person → 3rd-person → 2nd-person**. You are a **body + camera**: in 1st person the camera is at your eyes (your own body is hidden); in 3rd person it pulls behind + above; in 2nd person it sits in **front** and looks **back** at you (so you see your own face). 3rd + 2nd person both pull the camera in past walls in the way. Others always see your avatar either way |
| **F8** | Cycle the **active view** through your **body view** and every placed **camera object**: a **Fixed** camera shows a static shot from where you placed it; a **Follow** camera swivels to track a **target** — your body by default, or any object by id (its **Target** field). You still control your body normally while watching from a camera (your avatar is shown so you can see yourself). A local, per-peer choice — clients can look through cameras too |
| **F9** | Toggle **split-screen**: SINGLE (full screen) ↔ a **2-way LEFT \| RIGHT** split. The left half shows your current active view (F8); the right half shows the **next** view in the F8 cycle (the next placed camera, or your body view if there are none) — two cameras rendered at once. A local, per-peer view choice |
| **← / →** | Look left / right (yaw) |
| **↑ / ↓** | Look up / down (pitch) |
| **Q** / **E** | Roll camera left / right |
| **R** | Reset roll to level |
| **Z** / **X** | Zoom in / out (FOV narrower / wider, 20°–120°) |
| **V** | Reset FOV to default |
| **=** / **-** | Increase / decrease your camera light power |
| **P** | Cycle render detail 1 → 2 → 3 → 4 → 1 (lower = faster) |
| **T** | Open chat — a contained box (never overlaps the HUD) with word-wrapped messages; **PgUp/PgDn** or **↑/↓** scroll the history; **Enter** sends, **Esc** closes the chat |
| **Esc** | Quit the app (while chatting, Esc closes the chat instead) |

> **Gravity** is a per-world setting (enabled in the *Create world* dialog). With gravity **off**, the camera always free-flies as before. With gravity **on**, you start as a walking character — press **F1** any time to switch to free-fly for building/inspecting.

### Editor (press **Tab** for the overlay editor, or **`** for the docked editor)
The HUD has three modes: **PLAY** (minimal — a crosshair + chat hint over full-screen 3D), **OVERLAY-EDIT** (**Tab** — the editor panels overlaid on full-screen 3D), and **DOCKED-EDIT** (**`** — a Unity/Blender-style layout: the 3D in a centre viewport with a Toolbar on top, a Status bar on the bottom, a Hierarchy on the left, and a grouped Inspector on the right). The editing controls below are identical in both edit modes; only the presentation differs. Flight controls still work while editing. The layout reflows when you resize / zoom the terminal.

| Key | Action |
| --- | --- |
| **Tab** | Toggle the **overlay** editor (Play ↔ Overlay-Edit) |
| **`** | Toggle the **docked** editor (Play ↔ Docked-Edit) |
| **G** | Cycle the spawn type (cube / sphere / cylinder / cone / pyramid / ramp / flatpicture / light / **camera** / any `models/` mesh). A **camera** is a placeable viewpoint (set its **Kind** to Fixed or Follow; a Follow camera's **Target** is the object id it tracks, or the player by default) you switch to with **F8**. A camera's rotation follows the view convention: **Rot X = roll** (spins the image), **Rot Y = yaw**, **Rot Z = pitch** — use Y/Z to aim it |
| **B** | Spawn the current type in front of the camera *(authority only)* |
| **F** | Aim-select: pick the object under the crosshair |
| **[** / **]** | Select previous / next object |
| **,** / **.** | Move the field cursor up / down in the properties panel |
| **N** / **M** | Decrease / increase the value of the highlighted field *(authority only)* |
| **Enter** | Type an exact value into the highlighted field — the **Name** (text) or any **numeric** field (parsed + clamped on Enter; Esc cancels). Enum/toggle fields aren't typed — use N/M *(authority only)* |
| **I** / **K** | Move the selected object along +Z / −Z *(authority only)* |
| **J** / **L** | Move the selected object along −X / +X *(authority only)* |
| **U** / **O** | Move the selected object up / down (+Y / −Y) *(authority only)* |
| **Delete** | Delete the selected object *(authority only)* |
| **F2** | Toggle shadows on/off *(authority only, live-synced)* |
| **F3** | Toggle the BVH acceleration on/off *(authority only, live-synced)* |
| **F4** | Toggle the camera headlight on/off *(authority only, live-synced)* |
| **F6** | Toggle the floor platform on/off *(authority only, live-synced)* |
| **F5** | Save the world back to `worlds/<name>.json` *(authority only)* |

> In an online session, only the **authority** (the server, or a local solo session) can spawn/move/edit/delete/save. A connected client can open the editor to **select and inspect** objects, but its changes never affect the shared world.

### Chat (press **T** to open, online only)
| Key | Action |
| --- | --- |
| *type* | Enter your message |
| **Enter** | Send |
| **Backspace** | Delete a character |
| **Esc** | Cancel without sending |

### The properties panel (edited with `,` `.` `N` `M`, or **Enter** to type)
Use `,` / `.` to move the field cursor and **N** / **M** to step the highlighted field. For a faster, exact change, press **Enter** to *type* a value into the highlighted field: the **Name** (text) or any **numeric** field (Pos/Rot/Scale/Spin, R/G/B/A, Pale, Mass, Bounce, Friction, RollFric, TexScale, Radius, Power, and the light fields). The typed value is parsed and **clamped to the field's valid range** on **Enter**; **Esc** cancels (leaves it unchanged); an empty or invalid entry is ignored. Enum/toggle fields (Collide, Gravity, Collider, Texture, TexFace, TexFilter, Kind, Shape) are *not* typed — cycle them with N/M.

The list of editable fields depends on the selected object's type:

- **Mesh / cube / cylinder / cone / pyramid / ramp** — Pos X/Y/Z, Rot X/Y/Z, Scale, Spin (auto-rotate speed), **R / G / B / A** (color + object transparency), **Pale** (color paleness), **Texture** (cycle the PNGs in `textures/` + "none"), **TexScale** (UV tiling), **TexFace** (which side wears it — a cube's 6 faces, else "All"), **Collide**, **Gravity**, **Collider**, **Mass**, **Bounce**.
- **Flat picture** — Pos X/Y/Z, Rot X/Y/Z, Scale, **R / G / B / A**, **Pale**, **Texture**, **TexScale**, **TexFace** — a visual panel (no physics fields; it's non-colliding).
- **Sphere** — Pos X/Y/Z, Radius, **R / G / B / A**, **Pale**, **Texture**, **TexScale**, **TexFace**, **Collide**, **Gravity**, **Mass**, **Bounce**.
- **Platform** — Pos X/Y/Z, Shape/Size (or Width × Depth), **R / G / B / A**, **Pale**, **Collide**, **Gravity**.
- **Light** — Pos X/Y/Z, Power, **Influence**, **R / G / B / A**, **Pale**, **Kind**, then per kind, then **Gravity**:
  - *Point*: nothing extra.
  - *Directional*: Dir X/Y/Z, Spin.
  - *Spot*: Dir X/Y/Z, Cone (half-angle), **Beams** (1–8 fanned cones), **Shape** (Circle / Square / Triangle), Spin.
  - *Area*: Dir X/Y/Z, Size (half-extent), Spin.

Color is edited as four **R / G / B / A** channels (0–255 each): full 24-bit color plus an **A** (alpha) channel that is the **object transparency** (how see-through it is). A separate **Pale** value (0–1) is the **color transparency** — it washes the surface's own color toward white *without* affecting see-through or shadow. For a light, R/G/B set the color it emits, **Pale** fades that emission toward white, and **Influence** controls how strongly the color tints surfaces. **Collide** / **Gravity** are on/off toggles (N or M flips them); they read `Off (locked)` and can't be turned on when the world's Collision / Gravity switch is off. **Collider** toggles a mesh's collider shape between **AABB** (a fast world-axis box) and **OBB** (an oriented box that rotates with the object, hugging its true shape — better for tilted/elongated meshes). **Mass** weights the physics impulse solver — a heavier object is shoved less when a lighter one collides with it. **Bounce** is the object's restitution (`0` = dead, `1` = perfectly elastic); it shows `world (X)` when it inherits the world's Bounce, and on a contact the two surfaces' bounce values combine.

---

## Adding models
Drop a mesh into `SampleGame/models/` as `<name>.obj` (export with vertex normals for smooth shading; Blender does this by default). Models are a pure geometry library — there is **no per-model JSON** anymore; placement (position, rotation, scale, color, anchor, spin) lives in the world.

To place a model in a scene, either:
- Spawn it live in the editor (`G` to find it in the spawn list, then `B` to spawn), arrange it, and `F5` to save; or
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
  "rotateSpeed": 1.0,
  "collides": true,
  "gravity": false
}
```
- `type` — `mesh`, `cube`, `sphere`, `cylinder`, `cone`, `pyramid`, `ramp`, `flatpicture`, or `light`.
- `mesh` — the `models/` file name (without `.obj`), for `type: "mesh"`.
- `texture` — a `textures/` PNG file name (e.g. `"brick.png"`, `""` = none/flat colour): wraps the object with the image. Works on every primitive (cube/sphere/cylinder/cone/pyramid/ramp/flatpicture) **and imported `mesh` objects** (which map by the UVs authored in the `.obj`). In an online session the PNG **bytes are streamed to peers**, so a client without the file still sees the texture (it syncs by file name *and* pixels).
- `textureScale` — UV tiling factor (default `1`): `2` tiles the texture 2×2 across the surface, `0.5` shows half of it, etc.
- `textureFace` — which face wears the texture (default `-1` = all faces). For a cube, `0`–`5` select one side (`+X, -X, +Y, -Y, +Z, -Z`); the other faces then show flat colour. Other shapes are a single face (only `-1`/all applies).
- `textureFilter` — texture sampling: `0` = **nearest** (default), `1` = **bilinear** (smooths magnification), `2` = **mipmapped** (trilinear minification, so distant / grazing surfaces don't shimmer).
- `position` — world position in units (Y is the vertical axis).
- `rotation` — initial rotation in radians.
- `scale` — uniform scale.
- `color` — a hex string `#RRGGBB` (or `#RRGGBBAA` with alpha = **object** transparency), an `R,G,B` / `R,G,B,A` triple, or a .NET `ConsoleColor` name (e.g. `Red`). Saved back as hex.
- `colorFade` — **color** transparency / paleness, 0–1 (default `0`): washes the object's colour toward white, independent of the alpha (`A` = object transparency, `colorFade` = colour transparency).
- `anchor` — how the mesh sits at its position: `bottom` (base on the floor, default), `center` (geometric center), or `origin` (raw OBJ origin).
- `rotateSpeed` — auto-spin around the vertical axis, in radians per second (0 = static).
- `collides` — is this object solid (effective only when the world's `physics.collisionEnabled` is true). Default `true`.
- `gravity` — is this object pulled down by world gravity (effective only when `physics.gravityEnabled` is true). Default `false`.
- `collider` — a mesh's collider shape: `"aabb"` (world-axis box, default) or `"obb"` (oriented box that rotates with it).
- `mass` — impulse-solver mass (default `1`); a heavier object is shoved less in a collision.
- `restitution` — bounciness 0–1; `-1` (default) inherits the world's `physics.restitution`.

A `light` object additionally uses: `power`, `lightKind` (`point`/`directional`/`spot`/`area`), `direction`, `coneAngle`, `beamCount`, `coneShape` (`circle`/`square`/`triangle`), `lightSize`, and `lightSpin`.

The world file also has a top-level `physics` block with the master switches:
```json
"physics": { "gravityEnabled": false, "gravityStrength": 9.8, "collisionEnabled": true, "restitution": 0.0 }
```
(`restitution` is the world's default Bounce, 0–1; individual objects can override it.)

Edit a model or a world JSON and just re-run — no rebuild required.

---

## Project structure
- `Nova3DVisualiser/` — the engine (library): vector math, shapes, raytracing, the colored-lighting pipeline, truecolor screen output, BVH, OBJ/mesh loading, networking, logging. It is dependency-free.
- `Nova3DVisualiser.Gpu/` — the optional GPU renderer (depends on the engine + [ILGPU](https://ilgpu.net/)). Keeps the heavy GPU dependency out of the engine; provides `GpuScreen`, the NVIDIA/CUDA raytracing kernel, and the flat scene-snapshot upload.
- `SampleGame/` — the demo application: the setup wizard (its own small console-UI toolkit in `SampleGame/WizardUi/` — keyboard + mouse, no external UI library), the world system (`worlds/`), the in-scene editor, networking host, and the `models/` mesh library.

---

## Contributing
Nova3DVisualiser is written solely by its maintainer and **does not accept code contributions** — pull requests are closed unmerged, and forks are not merged back. **Bug reports, feature requests, and ideas are very welcome** via [GitHub Issues](https://github.com/Jareltis/Nova3DVisualiser/issues). You are free to fork and modify the project under GPL-3.0. See [CONTRIBUTING.md](CONTRIBUTING.md) for details.

---

## Credits
Nova3DVisualiser is a modified and extended version of **Neo3dEngine** by **Ivan Sobolev** (https://github.com/IvanSobolev/Neo3dEngine), forked at version **v0.1.1**.
- Original engine © Ivan Sobolev, licensed under GPL-3.0.
- Modifications and new features © Jareltis, 2026.

Major changes from the original include: the renamed engine; a rewritten floating-point, **colored** lighting pipeline with per-channel tone mapping and **24-bit truecolor** output; a full multi-light system (point / directional / spot / area, colored emission, light spin, multi-beam spots, and circle/square/triangle cone shapes); smooth shading and a Blender-robust OBJ loader; a JSON **world system** with an **in-scene editor** and live network sync; an **optional GPU renderer** (ILGPU / CUDA) at full CPU-parity; **PNG textures** (UV mapping, bilinear + mipmap filtering, network-synced pixels); a **multi-view camera system** (1st / 2nd / 3rd-person, placeable Fixed / Follow cameras, and split-screen); a reworked three-mode **HUD** (play / overlay-edit / docked Hierarchy + Inspector) with the whole UI — including the setup wizard — on the engine's own console renderer (no external UI toolkit); a **real rigid-body physics** layer (a walking first-person character; gravity where a ball collides with the real mesh surface and rolls down slopes with momentum; per-object collision with AABB/OBB colliders and full 3D Separating-Axis contacts; per-object restitution, mass, friction and 3D tumbling with drift-free quaternion orientation; frame-rate-independent substepping; all under world-level master switches); **diff rendering** of the console; folder-based mesh loading with anchoring; BVH acceleration; bounding-sphere culling for primary and shadow rays; a shadow toggle; timestamped logging; and main-loop improvements (quit key, FPS cap, high-resolution delta time).

## License
This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**. Because it is derived from Neo3dEngine (GPL-3.0), Nova3DVisualiser is and must remain GPL-3.0. See the [LICENSE](LICENSE) file for the full text.
