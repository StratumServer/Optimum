---
name: run-optimum
description: Build, deploy, launch, stop and screenshot the Optimum Vintage Story client on Vulkan or OpenGL, and confirm from the log which renderer actually started. Use for any "run it", "check in game", "compare backends" request.
---

# Run Optimum and verify what is on screen

1. Deploy: `make deploy` (Cecil patch, copies DLLs, shaders and the Vulkan backend into
   `.vanilla/win-x64/vintagestory`). If only the backend changed: `dotnet build Optimum.Render.Vulkan -c Release && cp bin/Release/net10.0/Optimum.Render.Vulkan.dll .vanilla/win-x64/vintagestory/`.
2. Stop any running client first: `scripts/dev/kill-client.sh` (its own call; no launch text in the same command).
3. Launch: `RENDERER=vulkan scripts/dev/run-client.sh "serene cave world"` (or `RENDERER=opengl`).
   Diagnostics go in the environment: `OPTIMUM_VULKAN_VALIDATION=1 OPTIMUM_RENDER_TRACE=/tmp/t.log`.
4. Wait for the world: poll the log for `Savegame .* loaded` and `Received level finalize`
   (about 25 s), never blind-sleep.
5. **Confirm the renderer:** `scripts/dev/client-renderer.sh`. If it says `OpenGL renderer: <reason>`,
   the Vulkan probe failed; read the reason (stale `Optimum.Render.Vulkan.dll` beside the client is the
   classic one) and fix that before judging pixels.
6. Screenshot: `scripts/dev/screenshot.sh /tmp/vulkan.png`, then Read the PNG and describe what you see.
   For a backend comparison take both shots from the same save and camera.
7. Stop: `scripts/dev/kill-client.sh`. Restore `ModConfig/optimum.json` `Renderer` to what the user had.

Gotchas: `ssaa` 0.5 in clientsettings halves the render resolution on both backends; the random
`--rndWorld -p creativebuilding` world is superflat and has no animals; passing `world.vcdbs` to `-o`
creates a new world named `world.vcdbs.vcdbs`.
