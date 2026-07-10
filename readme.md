# Nova3DVisualiser

*(На русском — [readmeRU.md](readmeRU.md))*

A multiplayer **ASCII 3D raytracer** that renders straight to the terminal, written in C# / **.NET 10**. It runs on a multithreaded CPU renderer by default, with an optional **GPU** path (ILGPU / CUDA) at pixel-for-pixel parity. It ships with a full in-scene editor, a JSON world system, real rigid-body physics, PNG textures, and TCP + UDP multiplayer — and its whole UI, including the setup wizard, runs on the engine's own console renderer, with **no external UI toolkit**.

It is developing toward an engine whose **built-in editor is itself the game** — an editor-first sandbox where the worlds, models and textures you create are **shared local content**.

## Features

- **CPU or GPU renderer, per world** — multithreaded CPU raytracer by default, or an ILGPU/CUDA GPU path at full feature parity (transparent fallback if no GPU).
- **Truecolor ASCII** — 24-bit RGB per cell via ANSI, with diff rendering (only changed cells are rewritten).
- **Multi-light system** — point / directional / spot / area lights, colored emission, spin, multi-beam spots, circle/square/triangle cones, alpha-correct shadows.
- **Two transparencies** — object alpha (see-through, front-to-back) and color paleness (washes toward white).
- **PNG textures + UV** — every primitive and imported mesh; tiling, per-face, and nearest/bilinear/mipmapped filtering; pixels stream to peers.
- **Real rigid-body physics** — an impulse-based solver: gravity, per-object collision (AABB/OBB), friction, rolling friction, restitution, mass, 3D tumbling, and solid stacks; **dynamic convex-hull bodies** so a custom mesh falls, tumbles and stacks as its true shape (and rests on the real triangles of sloped meshes); **joints** — ball-socket, hinge (with angle limits and a motor), and distance (rigid or spring) — authored in the editor and synced in multiplayer, where a joint can attach to **any** object (a non-physical side — a light, camera, static prop, or the platform — acts as a fixed anchor you can drag in the editor to move whatever hangs from it), a joint's two bodies can optionally **collide with each other** (a real chain bumps instead of folding through), and the editor shows why a joint is inactive; and a **capsule** first-person character with real body height.
- **World system + in-scene editor** — each scene is one `worlds/*.json`; spawn, move, edit, and save objects live.
- **Cameras & HUD** — 1st/2nd/3rd-person body views, placeable Fixed/Follow cameras, split-screen, and three HUD modes (play / overlay / docked).
- **Multiplayer** — TCP + UDP server/client: reliable TCP for world sync (chunked meshes), edits, spawns and chat; an unreliable UDP fast-path (latest-wins, MTU-chunked, TCP fallback) for player transforms and physics sync. Chat lines are prefixed with the sender's **nickname**, and a nickname roster travels to every peer (used for chat now, and hover labels next).

## Quick start

**Requirements:** .NET 10 SDK · Windows · a 24-bit-color terminal (Windows Terminal recommended) · *(optional)* an NVIDIA/CUDA GPU.

```bash
dotnet build -c Release
dotnet run --project SampleGame -c Release
```

The branded **setup wizard** takes it from there — nickname, session mode, network role, world (create/load), and network details. It opens by asking who is playing: pick a previously used nickname or type a new one. Nicknames are remembered locally in `SampleGame/users.json` (git-ignored) and do not gate or isolate any content — worlds, models, and textures stay shared local files regardless of the nickname.

### Test Lab

Generate a ready-made **"TestLab"** world to check physics against known-good setups:

```bash
dotnet run --project SampleGame -c Release -- maketestworld
```

Then start the game and **load the "TestLab" world**. It has three lanes:

- **Zone J** (joints showroom) — a ball-socket pendulum, a rigid rod, a spring, a hinged door with ±90° limits, a motorised windmill that spins in front of its post, a 3-link chain whose links **collide like a real chain** (they bump instead of folding through each other), a two-rod swing, and a **trapdoor** that flops open on load and hangs at its hinge limit (limits demonstrated with no input). Regenerate any time with `maketestworld` to refresh an existing TestLab.
- **Zone P** (physics playground) — a cube stack, a bouncy sphere, a ramp with a sphere that rolls off, and a heavy/light pair for shove tests.
- **Zone C** (capsule course) — a head-height bar you can't fit under, a slalom, and a low step to walk onto.

## Documentation

Full documentation lives in the **[GitHub Wiki](https://github.com/Jareltis/Nova3DVisualiser/wiki)**:

- **[Getting Started](https://github.com/Jareltis/Nova3DVisualiser/wiki/Getting-Started)** — requirements, build & run, the setup wizard.
- **[Controls](https://github.com/Jareltis/Nova3DVisualiser/wiki/Controls)** — the full key reference, HUD modes, editor, chat.
- **[World Format](https://github.com/Jareltis/Nova3DVisualiser/wiki/World-Format)** — the world JSON: objects, types, per-object fields.
- **[Textures](https://github.com/Jareltis/Nova3DVisualiser/wiki/Textures)**, **[Physics](https://github.com/Jareltis/Nova3DVisualiser/wiki/Physics)**, **[Multiplayer](https://github.com/Jareltis/Nova3DVisualiser/wiki/Multiplayer)**, **[GPU Renderer](https://github.com/Jareltis/Nova3DVisualiser/wiki/GPU-Renderer)**, and more.

## Contributing

Nova3DVisualiser is written solely by its maintainer and **does not accept code contributions** — pull requests are closed unmerged. **Bug reports, feature requests, and ideas are very welcome** via [GitHub Issues](https://github.com/Jareltis/Nova3DVisualiser/issues). You are free to fork and modify under GPL-3.0. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Licensed under the **GNU General Public License v3.0 (GPL-3.0)** — see [LICENSE](LICENSE). Nova3DVisualiser is a modified and extended version of **Neo3dEngine** by **Ivan Sobolev** (forked at v0.1.1), so it is and must remain GPL-3.0; see [NOTICE.md](NOTICE.md) for attribution.

## Credits

- Original engine © **Ivan Sobolev** ([Neo3dEngine](https://github.com/IvanSobolev/Neo3dEngine)), GPL-3.0.
- Modifications and new features © **Jareltis**, 2026.
