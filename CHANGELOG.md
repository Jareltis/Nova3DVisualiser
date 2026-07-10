# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.1] — Unreleased

### Added
- A UDP fast-path for real-time multiplayer: player transforms and physics-sync
  batches now travel over an unreliable, sequence-filtered UDP channel on the same
  port, with server-side endpoint learning, automatic fallback to TCP, and
  MTU-aware chunking of large physics batches. Reliable TCP continues to carry
  world sync, meshes, textures, live edits, chat, and the join handshake.
- **Joints** connecting objects, solved in the same constraint solver as contacts:
  ball-socket, hinge (with optional angle limits and a torque-limited motor), and
  distance (a rigid rod or a soft spring). Authored in the editor as a "joint"
  object with a colour-coded line/axis marker, saved in the world, and synced in
  multiplayer.
- **Dynamic convex-hull physics for mesh objects**: a custom mesh with gravity +
  collision now falls, tumbles, and stacks as its true convex shape — resting on its
  faces and sliding on the real triangles of sloped meshes — instead of a bounding
  box. (Box-like primitives keep their cheaper box collider.)
- A **capsule player**: the walking player now has real body height instead of a
  point bubble, so it can't slip under low overhangs or poke its head through a
  head-height bar, and it slides along walls without catching.

### Security
- Bounded wire-driven allocations (a maximum framed-packet size and per-packet
  collection counts).
- Received mesh and texture filenames are sanitized and confined to the assets
  folder (path-traversal and rooted-path rejection, enforced extensions).
- PNG decoding caps image dimensions and bounds inflate output (decompression-bomb
  protection).
- Chunk reassembly is bounded (maximum parts, concurrent reassemblies, and
  buffered bytes, with least-recently-used eviction).
- The incoming packet queue, the concurrent-connection count, and per-peer packet
  rate are bounded.
- Added log levels, size-based log rotation, rate-limited anomaly logging,
  transport counters, and a periodic activity summary.

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
