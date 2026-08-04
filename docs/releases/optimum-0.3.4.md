# Optimum 0.3.4

Optimum 0.3.4 targets Vintage Story 1.22.5.

## Fixes

- The source release carries a valid unified diff header for `SystemRenderOITLayers.cs.patch`.
- Bootstrap rejects malformed patches before compilation and reports the affected patch.
- The launcher aborts before game startup when a required patch, output validation, or JIT preflight fails.

## Validation

Patch syntax validation accepted 117 patches. The installer-release and fail-closed tests passed. Launcher tests passed.
