# Nova3DVisualiser — NOTICE

Nova3DVisualiser is a modified and extended version of **Neo3dEngine**.

## Original work
**Neo3dEngine** — Copyright © Ivan Sobolev
Source: https://github.com/IvanSobolev/Neo3dEngine
Licensed under the GNU General Public License v3.0 (GPL-3.0).
Nova3DVisualiser was forked from Neo3dEngine at version **v0.1.1**.

## Modifications
Copyright © 2026 Jareltis — licensed under the GNU General Public License v3.0 (GPL-3.0).

Notable changes from the original:
- Renamed the engine and all namespaces to Nova3DVisualiser.
- Rewrote the lighting pipeline to floating point end to end, with multiple summed point lights, an ambient term, and Reinhard tone mapping.
- Added smooth (per-vertex-normal) shading and a Blender-robust OBJ loader (n-gon fan triangulation, negative/relative indices, geometric-normal fallback).
- Added folder-based model loading with per-model JSON configuration (position, rotation, scale, color, spin) and a mesh anchoring system.
- Added a BVH acceleration structure for high-poly meshes, with per-ray local-space traversal so it works with transformed/animated models.
- Added per-object bounding-sphere culling for both primary and shadow rays.
- Added an optional shadow toggle and launch-time configuration prompts.
- Added thread-safe, timestamped file logging.
- Reworked the main loop: high-resolution Stopwatch delta time, an FPS cap, and a clean quit key.

## License
Because Nova3DVisualiser is derived from Neo3dEngine (GPL-3.0), the entire work is licensed under the GNU General Public License v3.0. The full text is in the [LICENSE](LICENSE) file.

## Third-party components
This project references the following third-party packages via NuGet (their source is not redistributed in this repository):
- **Terminal.Gui** — © Miguel de Icaza, Tig Kindel, and contributors (the gui-cs project) — MIT License.

The combined work remains licensed under GPL-3.0.
