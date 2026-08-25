# Contributing

Optimum accepts pull requests for performance optimizations, bugfixes, and build improvements.

## Rules

1. Zero gameplay impact. Optimizations must not change what the player sees or how the game behaves.
2. Measurable. If you claim a performance gain, describe how to reproduce and measure it.
3. Configurable where appropriate. If the optimization has any visual tradeoff (even subtle), it needs a toggle in the Extra settings tab.
4. No new dependencies without discussion. Open an issue first.
5. Tests for non-trivial logic. The project uses xunit (178 tests).

## Setup

```bash
make build   # bootstrap (decompile vanilla) + compile
make test    # verify all 178 tests pass
make run     # launch and test in-game
```

See the [Building from Source](https://github.com/StratumServer/Optimum/wiki/Building-from-Source) wiki page for prerequisites and details.

## Architecture

Optimum has three components:

| Component | Purpose |
|-----------|---------|
| `Optimum.Launcher` | Entry point exe. Cache validation, assembly loading, fallback. No VS refs at compile time. |
| `Optimum.Patcher` | Cecil-based IL patcher. Transplants methods, injects members/types, applies IL hooks. |
| Source forks | VSEssentials, VSSurvivalMod, VintagestoryAPI patches applied at compile time (the "donor" DLLs). |

The patcher runs as a subprocess of the launcher. The donor DLL (compiled from the source forks) provides the optimized method bodies that get transplanted into the vanilla DLL.

## How Patches Work

1. Optimizations are written as source code changes in the fork projects (VintagestoryApi/, VSEssentials/, VSSurvivalMod/, build/VintagestoryLib/).
2. `extract-patches.sh` generates `.patch` files from the diff against `.baseline/`.
3. `Optimum.Patcher/Program.cs` defines which methods/members/types get transplanted via Cecil.
4. At runtime, the launcher invokes the patcher to apply these transplants to the vanilla DLL.

To add a new optimization:
1. Make the change in the appropriate fork project.
2. Run `make build` to verify it compiles.
3. Run `make test` to verify tests pass.
4. If it's a VintagestoryLib change, add the method to `Optimum.Patcher/Program.cs` targets.
5. Run `scripts/extract-patches.sh` to regenerate patches.
6. Run `scripts/check-patches.sh` to verify patch consistency.

## Pull Request Process

`main` is the working and release branch. Pull requests target `main`. The
`dev` branch this project used before is gone; it was deleted after being
merged into `main`.

1. Fork the repo and create a branch from `main`.
2. Make your changes. Keep commits focused.
3. Run `make test` and verify all tests pass.
4. Run `make run` and verify the client launches without shader errors or crashes.
5. Open a PR with a clear title and description of what the optimization does and what it saves.

## Commit Style

Imperative mood, under 72 characters. Conventional Commits format:

```
feat(rendering): add frustum cache for particle systems
fix(audio): prevent volume update when delta is zero
perf(shaders): reduce blur taps from 11 to 7
chore(patches): regenerate after baseline update
```

## What We Accept

- Performance optimizations with zero gameplay impact
- Bugfixes for client issues (reference upstream issue numbers)
- Build system and installer improvements
- Documentation fixes
- Test coverage expansion

## What We Do Not Accept

- Gameplay changes (new items, balance tweaks, server-side features)
- Cosmetic-only changes without performance justification
- Dependencies on external modding frameworks (Harmony, etc.)
- Code that breaks compatibility with vanilla servers

## License

By submitting a PR, you agree that your contribution uses the license that applies to the files you change. See `LICENSE-SCOPE.md` before adding code.
