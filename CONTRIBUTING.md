# Contributing

Optimum accepts pull requests for performance optimizations, bugfixes, and build improvements.

## Rules

1. Zero gameplay impact. Optimizations must not change what the player sees or how the game behaves.
2. Measurable. If you claim a performance gain, describe how to reproduce and measure it.
3. Configurable where appropriate. If the optimization has any visual tradeoff (even subtle), it needs a toggle in the Extra settings tab.
4. No new dependencies without discussion. Open an issue first.
5. Tests for non-trivial logic. The project uses xunit.

## Setup

```bash
make build   # bootstrap + compile
make test    # verify everything passes
make run     # launch and test in-game
```

See the [Building from Source](https://github.com/Zaldaryon/Optimum/wiki/Building-from-Source) wiki page for prerequisites and details.

## Pull Request Process

1. Fork the repo and create a branch from `main`.
2. Make your changes. Keep commits focused.
3. Run `make test` and verify all 81 tests pass.
4. Run `make run` and verify the client launches without shader errors or crashes.
5. Open a PR with a clear title and description of what the optimization does and what it saves.

## Commit Style

Imperative mood, under 72 characters. Conventional Commits format:

```
feat(rendering): add frustum cache for particle systems
fix(audio): prevent volume update when delta is zero
perf(shaders): reduce blur taps from 11 to 7
```

## What We Accept

- Performance optimizations with zero gameplay impact
- Bugfixes for client issues (reference upstream issue numbers)
- Build system improvements
- Documentation fixes
- Test coverage expansion

## What We Do Not Accept

- Gameplay changes (new items, balance tweaks, server-side features)
- Cosmetic-only changes without performance justification
- Dependencies on external modding frameworks (Harmony, etc.)
- Code that breaks compatibility with vanilla servers

## License

By submitting a PR, you agree that your contribution is licensed under the same terms as the project (GPL-3.0 + Commons Clause).
