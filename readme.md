# Nova3DVisualiser

A real-time 3D engine that renders to the terminal as ASCII art, written in C# / .NET 8. It is a CPU raytracer: every frame it traces rays into the scene and maps brightness to a character gradient, drawing the result directly in the console.

## Features
- CPU raytracing rendered to ASCII via a brightness gradient.
- Floating-point lighting pipeline: multiple summed point lights, an ambient term, and Reinhard tone mapping.
- Smooth (per-vertex-normal) shading, with a Blender-friendly OBJ loader (n-gon fan triangulation, negative/relative indices, geometric-normal fallback).
- Folder-based model loading: drop `<name>.obj` + `<name>.json` into `models/` and configure position, rotation, scale, color, spin, and anchor — no rebuild needed.
- BVH acceleration for high-poly meshes, built once in local space and traversed with the ray transformed per frame, so it works even with animated/spinning models.
- Per-object bounding-sphere culling for both primary and shadow rays.
- Optional shadows (toggle at launch).
- Basic TCP networking (server/client) with a shared scene and in-app chat.
- Timestamped file logging.
- Frame pacing (FPS cap) and a quit key.

## Requirements
- .NET 8 SDK.
- Windows (keyboard input uses the Win32 API, so the app is currently Windows-only).

## Build & Run
```bash
dotnet build -c Release
```
Set `SampleGame` as the startup project in Visual Studio, or run it directly:
```bash
dotnet run --project SampleGame -c Release
```
On launch you choose Server or Client, the IP and port, and a few toggles (extra light, disable your own light, shadows, BVH).

## Controls
- **W A S D** — move horizontally (relative to where you are looking).
- **Space / C** — move up / down.
- **Arrow keys** — look around.
- **T** — open chat (network mode).
- **Escape** — quit.

## Adding models
Place a mesh and its config side by side in `SampleGame/models/`:
- `dragon.obj` — the geometry (export with vertex normals for smooth shading; Blender does this by default).
- `dragon.json` — its configuration:
```json
{
  "name": "Dragon",
  "position": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "rotation": { "x": 0.0, "y": 0.0, "z": 0.0 },
  "scale": 1.0,
  "color": "Yellow",
  "rotateSpeed": 1.0,
  "anchor": "bottom"
}
```
- `position` — world position in units (Y is the vertical axis).
- `rotation` — initial rotation in radians.
- `scale` — uniform scale.
- `color` — a .NET `ConsoleColor` name (e.g. `Red`, `Cyan`, `Yellow`).
- `rotateSpeed` — auto-spin around the vertical axis, in radians per second (0 = static).
- `anchor` — how the mesh sits at its position: `bottom` (base on the floor, default), `center` (geometric center), or `origin` (raw OBJ origin).

Edit a model or its JSON and just re-run — no rebuild required.

## Project structure
- `Nova3DVisualiser/` — the engine (library): vector math, shapes, raytracing, lighting, BVH, OBJ/model loading, networking, logging.
- `SampleGame/` — the demo application and networking host, plus the `models/` asset folder.

## Credits
Nova3DVisualiser is a modified and extended version of **Neo3dEngine** by **Ivan Sobolev** (https://github.com/IvanSobolev/Neo3dEngine), forked at version **v0.1.1**.
- Original engine © Ivan Sobolev, licensed under GPL-3.0.
- Modifications and new features © Jareltis, 2026.

Major changes from the original include: the renamed engine; a rewritten floating-point lighting pipeline with tone mapping; smooth shading and a Blender-robust OBJ loader; folder-based model loading with JSON configuration and anchoring; BVH acceleration; bounding-sphere culling for primary and shadow rays; a shadow toggle; timestamped logging; and main-loop improvements (quit key, FPS cap, high-resolution delta time).

## License
This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**. Because it is derived from Neo3dEngine (GPL-3.0), Nova3DVisualiser is and must remain GPL-3.0. See the [LICENSE](LICENSE) file for the full text.
