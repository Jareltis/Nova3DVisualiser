# Nova3DVisualiser

*(На русском — [readmeRU.md](readmeRU.md))*

A multiplayer **ASCII 3D raytracer** that renders straight to the terminal, written in C# / **.NET 10**. It runs on a multithreaded CPU renderer by default, with an optional **GPU** path (ILGPU / CUDA) at pixel-for-pixel parity. It ships with a full in-scene editor, a JSON world system, real rigid-body physics, PNG textures, and TCP multiplayer — and its whole UI, including the setup wizard, runs on the engine's own console renderer, with **no external UI toolkit**.

## Features

- **CPU or GPU renderer, per world** — multithreaded CPU raytracer by default, or an ILGPU/CUDA GPU path at full feature parity (transparent fallback if no GPU).
- **Truecolor ASCII** — 24-bit RGB per cell via ANSI, with diff rendering (only changed cells are rewritten).
- **Multi-light system** — point / directional / spot / area lights, colored emission, spin, multi-beam spots, circle/square/triangle cones, alpha-correct shadows.
- **Two transparencies** — object alpha (see-through, front-to-back) and color paleness (washes toward white).
- **PNG textures + UV** — every primitive and imported mesh; tiling, per-face, and nearest/bilinear/mipmapped filtering; pixels stream to peers.
- **Real rigid-body physics** — an impulse-based solver: gravity, per-object collision (AABB/OBB), friction, rolling friction, restitution, mass, 3D tumbling, solid stacks, and a walking first-person character.
- **World system + in-scene editor** — each scene is one `worlds/*.json`; spawn, move, edit, and save objects live.
- **Cameras & HUD** — 1st/2nd/3rd-person body views, placeable Fixed/Follow cameras, split-screen, and three HUD modes (play / overlay / docked).
- **Multiplayer** — TCP server/client with world sync (chunked meshes), physics sync, live edits, and chat.

## Quick start

**Requirements:** .NET 10 SDK · Windows · a 24-bit-color terminal (Windows Terminal recommended) · *(optional)* an NVIDIA/CUDA GPU.

```bash
dotnet build -c Release
dotnet run --project SampleGame -c Release
```

The branded **setup wizard** takes it from there — session mode, network role, world (create/load), and network details.

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
