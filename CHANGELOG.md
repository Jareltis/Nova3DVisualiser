# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] — 2026-07-05

First tagged release. Nova3DVisualiser is a real-time 3D engine that renders to the terminal
as ASCII art, on .NET 10. It is derived from and extends
[Neo3dEngine](https://github.com/IvanSobolev/Neo3dEngine) by Ivan Sobolev (GPL-3.0).

### Added

- **Rendering** — a multithreaded CPU raytracer with **24-bit truecolor** output and diff
  rendering; an optional **GPU renderer** (NVIDIA via ILGPU/CUDA, also OpenCL) at **exact
  CPU↔GPU parity** (validated pixel-for-pixel by `gputest`); a two-level BVH; smooth
  (per-vertex-normal) shading and a Blender-robust OBJ loader; and two independent
  transparency knobs (object alpha + colour paleness).
- **Lighting** — point / directional / spot / area lights, each shadow-tested independently
  and additive; alpha-correct shadows; coloured RGB emission; spot beam fans and
  circle / square / triangle cone shapes; and light spin.
- **Physics** — a unified **impulse-based rigid-body solver** (Coulomb + rolling friction,
  restitution, split-impulse positional correction, sleeping islands); collisions against
  real mesh triangles (ramps / pyramids), box stacking and emergent tumbling; per-object
  mass, collider (AABB / OBB), restitution, and friction; frame-rate-independent
  substepping.
- **Textures** — per-object **PNG textures + UV** with nearest / bilinear / **mipmapped**
  (trilinear) filtering; procedural UVs for every primitive (equirectangular for the sphere)
  and `.obj` UVs for imported meshes; CPU↔GPU texel parity; texture pixels stream to peers
  in multiplayer.
- **Multiplayer** — a TCP server/client with a shared world, chat, and remote-player
  avatars; the server streams the world (large meshes and textures chunked), live edit
  deltas, graphics-setting changes, and physics sync.
- **Cameras & views** — 1st / 2nd / 3rd-person body views; placeable **Fixed / Follow**
  cameras; F8 view switching; and F9 **2-way split-screen**.
- **HUD & editor** — three HUD modes: play, overlay-edit (`Tab`), and a **docked Unity /
  Blender-style editor** (`` ` ``) with Toolbar / Status / Hierarchy / Inspector panels and a
  contained chat; plus a live in-scene editor (spawn / move / edit / delete / save to JSON).
- **World system** — each scene is one `worlds/<name>.json`; `models/` is a pure `.obj` mesh
  library.
- **Setup wizard** — a branded Mode → Role → World → Create/Load → Network flow rendered by
  the engine's **own console UI** (keyboard + mouse, reflows on resize / zoom), with no
  external UI dependency.

[1.0.0]: https://github.com/Jareltis/Nova3DVisualiser/releases/tag/v1.0.0
