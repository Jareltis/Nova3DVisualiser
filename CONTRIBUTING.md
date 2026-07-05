# Contributing to Nova3DVisualiser

Thanks for your interest in the project! Please read this first — the contribution model
here is deliberately narrow.

## Contribution policy: no code contributions

**Nova3DVisualiser is developed solely by its maintainer.** The project does **not** accept
code contributions:

- **Pull requests will be closed without merging.**
- **External forks will not be merged** back into the main project.

This is a deliberate choice — a single-author codebase kept coherent and consistent by one
person — and **not** a reflection on the quality of anyone's work or ideas. Some well-known
open-source projects follow the same model (SQLite, for example, is written entirely by its
core team and does not accept outside code).

## What IS welcome

Feedback and ideas are genuinely appreciated. Please use **GitHub Issues**:

- **Bug reports** — use the [Bug report](.github/ISSUE_TEMPLATE/bug_report.md) template.
- **Feature requests and ideas** — use the
  [Feature request](.github/ISSUE_TEMPLATE/feature_request.md) template.
- **Questions, feedback, and discussion** — open an issue.
- **Security reports** — do **not** open a public issue; follow the private process in
  [SECURITY.md](SECURITY.md).

Well-described bug reports and feature requests are the most valuable way to help the
project, and they may well shape what the maintainer builds next.

## Forks

Nova3DVisualiser is licensed under **GPL-3.0**, which explicitly permits you to **fork,
modify, and redistribute** this code — provided your version also remains under GPL-3.0.
You are entirely free to do so. Such forks live independently; they simply won't be merged
back into this repository.

## Building & testing your own copy or fork

If you build the project for yourself (or work on a fork), here is how to build, run, and
verify it.

### Prerequisites

- **.NET 10 SDK.**
- **Windows.** Keyboard input and the setup wizard's mouse support use the Win32 console
  API, so the app is currently Windows-only. A terminal with 24-bit ANSI colour (Windows
  Terminal) gives the best output.
- **Optional:** an NVIDIA GPU with a current driver for the GPU renderer (via ILGPU/CUDA;
  no CUDA Toolkit needed). Without one, the app runs on the CPU renderer.

### Build & run

```bash
dotnet build -c Release --no-incremental
dotnet run --project SampleGame -c Release
```

The build should be **0 warnings / 0 errors**. Use `--no-incremental`; a plain incremental
build can report a false "0 warnings".

### Self-test suite

The app ships non-interactive self-tests (they run before the interactive wizard). They
should **all pass**, and `gputest` should be an exact CPU↔GPU match (Δ = 0):

```bash
for t in splashtest impulsetest cutovertest bvhtest worldtest editortest picktest \
         worldsynctest collisiontest physicstest texturetest gputest uitest; do
  dotnet run --project SampleGame -c Release --no-build -- $t
done
```

On Windows PowerShell:

```powershell
'splashtest','impulsetest','cutovertest','bvhtest','worldtest','editortest','picktest',
'worldsynctest','collisiontest','physicstest','texturetest','gputest','uitest' |
  ForEach-Object { dotnet run --project SampleGame -c Release --no-build -- $_ }
```

`gputest` runs on whatever accelerator ILGPU finds (CUDA/OpenCL if present, otherwise a
managed CPU accelerator), so it works even without a GPU.

If you extend a fork, a couple of invariants keep the codebase healthy: the engine project
(`Nova3DVisualiser/`) stays free of third-party packages (all GPU code lives in
`Nova3DVisualiser.Gpu/`, which uses ILGPU), and CPU↔GPU rendering parity is maintained
(any change to shading / intersection / compositing / BVH / textures is applied to both the
CPU path and the GPU kernel, keeping `gputest` at Δ = 0).

## Licensing

Nova3DVisualiser is licensed under **GPL-3.0** (it is derived from Neo3dEngine, GPL-3.0).
The project, and any fork or redistribution of it, remains under GPL-3.0.
