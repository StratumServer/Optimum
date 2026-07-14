# Optimum development targets.
# Requires (dev):       .NET 10 SDK, bash, python3, git, curl, perl.
# Requires (packaging): tar, curl. Optional per target:
#                         macOS .dmg on Linux -> cmake + mkisofs/genisoimage
#                         Windows package off-Windows -> pwsh + innoextract
# Run `make check` to see exactly what your host is missing.
# Windows: use Git Bash, WSL, or adapt to PowerShell.
#
# Packaging note: the optimized DLLs are platform-agnostic IL, so any host can
# target any platform (quality varies - see README "Host x target matrix").
# Vintage Story ships native ARM only for macOS, so Linux/Windows packages are
# x64-only; ARM there runs x64 via emulation (box64 / Windows-on-ARM).

# --- Configuration (override via env or make VAR=value) ---
CONFIGURATION ?= Release
VERSION ?= 1.22.3
CLIENT_ARCHIVE ?=

# Paths (all overridable)
VANILLA_DIR ?= .vanilla/win-x64/vintagestory
INSTALL_DIR ?= $(HOME)/.local/share/optimum
DATA_PATH ?= $(HOME)/.config/OptimumVintagestoryData
LOG_PATH ?=
EXTRA_MOD_PATH ?= $(CURDIR)/mods
EXTRA_ORIGIN ?=

# Launch options
CONNECT ?=
OPEN_WORLD ?=
PLAY_STYLE ?= creativebuilding

# Build output locations
BUILD_OUT = build/Vintagestory/bin/$(CONFIGURATION)/net10.0
MOD_OUT = bin/$(CONFIGURATION)/net10.0

BOOTSTRAP_ARGS :=
ifneq ($(CLIENT_ARCHIVE),)
  BOOTSTRAP_ARGS += --client-archive $(CLIENT_ARCHIVE)
endif
ifneq ($(VERSION),1.22.3)
  BOOTSTRAP_ARGS += --version $(VERSION)
endif

.PHONY: help check check-patches check-compat check-shaders bootstrap build clean refresh patches patch-il deploy run run-creative run-connect \
        package package-linux package-appimage package-macos package-win bench-scaling

help: ## Show available targets
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | sort | awk -F ':.*## ' '{printf "  %-14s %s\n", $$1, $$2}'

check: ## Report which bootstrap/packaging tools are installed (installs nothing)
	bash scripts/check-prereqs.sh

check-patches: ## Verify patch files against the current working tree
	bash scripts/check-patches.sh

check-compat: ## Verify patches keep vanilla multiplayer compatibility guards
	bash scripts/check-vanilla-compat.sh

check-shaders: ## Verify optimized shader overlays are not truncated
	bash scripts/validate-shader-assets.sh sources/shaders

bootstrap: ## Download client, decompile, clone forks, apply patches
	bash scripts/bootstrap.sh $(BOOTSTRAP_ARGS)

build: ## Build Release (runs bootstrap if missing or incomplete)
	@if [ ! -f .bootstrap-complete ]; then $(MAKE) bootstrap; fi
	unset Platform; dotnet build VintageStory.slnx -c $(CONFIGURATION)

clean: ## Remove intermediate build files
	find . -type d -name obj -not -path './.build/*' -not -path './.vanilla/*' | xargs rm -rf
	find . -type d -name bin -not -path './.build/*' -not -path './.vanilla/*' | xargs rm -rf

refresh: ## Force full re-bootstrap from scratch
	bash scripts/bootstrap.sh --refresh $(BOOTSTRAP_ARGS)

patches: ## Extract patches from working tree
	bash scripts/extract-patches.sh

patch-il: build ## Run Cecil patcher: vanilla DLL + compiled donor → patched output
	@echo "Running IL patcher..."
	@dotnet run --project Optimum.Patcher -c $(CONFIGURATION) -- \
		$(VANILLA_DIR)/VintagestoryLib.vanilla.dll \
		build/VintagestoryLib/bin/$(CONFIGURATION)/net10.0/VintagestoryLib.dll \
		build/VintagestoryLib/bin/$(CONFIGURATION)/net10.0/VintagestoryLib-patched.dll
	@echo ""

deploy: patch-il check-shaders ## Deploy Cecil-patched DLLs into vanilla client dir and install dir
	@echo "Deploying to $(VANILLA_DIR)..."
	@cp build/VintagestoryLib/bin/$(CONFIGURATION)/net10.0/VintagestoryLib-patched.dll $(VANILLA_DIR)/VintagestoryLib.dll
	@cp $(BUILD_OUT)/Vintagestory.dll $(VANILLA_DIR)/
	@cp $(BUILD_OUT)/Vintagestory.runtimeconfig.json $(VANILLA_DIR)/
	@cp $(MOD_OUT)/VintagestoryAPI.dll $(VANILLA_DIR)/
	@cp $(MOD_OUT)/VSEssentials.dll $(VANILLA_DIR)/Mods/
	@cp $(MOD_OUT)/VSSurvivalMod.dll $(VANILLA_DIR)/Mods/
	@cp $(MOD_OUT)/VSCreativeMod.dll $(VANILLA_DIR)/Mods/
	@cp $(MOD_OUT)/cairo-sharp.dll $(VANILLA_DIR)/Lib/
	@cp sources/shaders/*.fsh sources/shaders/*.vsh $(VANILLA_DIR)/assets/game/shaders/
	@if [ -d "sources/lang" ]; then for f in sources/lang/*.json; do [ -f "$$f" ] || continue; dst="$(VANILLA_DIR)/assets/game/lang/$$(basename $$f)"; [ -f "$$dst" ] || continue; python3 -c "import json,sys; s=json.load(open(sys.argv[1],encoding='utf-8-sig')); d=json.load(open(sys.argv[2],encoding='utf-8-sig')); d.update(s); json.dump(d,open(sys.argv[2],'w',encoding='utf-8'),ensure_ascii=False,indent='\t')" "$$f" "$$dst"; done; fi
	@if [ -d "$(INSTALL_DIR)" ]; then \
		echo "Deploying to $(INSTALL_DIR)..."; \
		cp build/VintagestoryLib/bin/$(CONFIGURATION)/net10.0/VintagestoryLib-patched.dll $(INSTALL_DIR)/VintagestoryLib.dll; \
		cp $(BUILD_OUT)/Vintagestory.dll $(INSTALL_DIR)/; \
		cp $(BUILD_OUT)/Vintagestory.runtimeconfig.json $(INSTALL_DIR)/; \
		cp $(MOD_OUT)/VintagestoryAPI.dll $(INSTALL_DIR)/; \
		cp $(MOD_OUT)/VSEssentials.dll $(INSTALL_DIR)/Mods/; \
		cp $(MOD_OUT)/VSSurvivalMod.dll $(INSTALL_DIR)/Mods/; \
		cp $(MOD_OUT)/VSCreativeMod.dll $(INSTALL_DIR)/Mods/; \
		cp $(MOD_OUT)/cairo-sharp.dll $(INSTALL_DIR)/Lib/; \
		cp sources/shaders/*.fsh sources/shaders/*.vsh $(INSTALL_DIR)/assets/game/shaders/; \
		if [ -d "sources/lang" ]; then for f in sources/lang/*.json; do [ -f "$$f" ] || continue; dst="$(INSTALL_DIR)/assets/game/lang/$$(basename $$f)"; [ -f "$$dst" ] || continue; python3 -c "import json,sys; s=json.load(open(sys.argv[1],encoding='utf-8-sig')); d=json.load(open(sys.argv[2],encoding='utf-8-sig')); d.update(s); json.dump(d,open(sys.argv[2],'w',encoding='utf-8'),ensure_ascii=False,indent='\t')" "$$f" "$$dst"; done; fi; \
	fi
	@echo "Deploy complete."

run: deploy ## Build, deploy, and launch client
	@echo "Launching Vintage Story client..."
	cd $(VANILLA_DIR) && dotnet Vintagestory.dll \
		--dataPath "$(DATA_PATH)" \
		$(if $(LOG_PATH),--logPath "$(LOG_PATH)") \
		$(if $(EXTRA_MOD_PATH),--addModPath "$(EXTRA_MOD_PATH)") \
		$(if $(EXTRA_ORIGIN),--addOrigin "$(EXTRA_ORIGIN)") \
		$(if $(CONNECT),-c "$(CONNECT)") \
		$(if $(OPEN_WORLD),-o "$(OPEN_WORLD)") \
		$(if $(filter run-creative,$(MAKECMDGOALS)),--rndWorld -p creativebuilding)

run-creative: deploy ## Launch straight into a new creative world
	cd $(VANILLA_DIR) && dotnet Vintagestory.dll \
		--dataPath "$(DATA_PATH)" \
		$(if $(LOG_PATH),--logPath "$(LOG_PATH)") \
		$(if $(EXTRA_MOD_PATH),--addModPath "$(EXTRA_MOD_PATH)") \
		--rndWorld -p creativebuilding

run-connect: deploy ## Launch and connect to a server (set CONNECT=ip:port)
	@if [ -z "$(CONNECT)" ]; then echo "Usage: make run-connect CONNECT=ip:port"; exit 1; fi
	cd $(VANILLA_DIR) && dotnet Vintagestory.dll \
		--dataPath "$(DATA_PATH)" \
		$(if $(LOG_PATH),--logPath "$(LOG_PATH)") \
		$(if $(EXTRA_MOD_PATH),--addModPath "$(EXTRA_MOD_PATH)") \
		-c "$(CONNECT)"

logs: ## Tail the client log
	@tail -f "$(DATA_PATH)/Logs/client-main.txt" 2>/dev/null || echo "No log file at $(DATA_PATH)/Logs/client-main.txt"

settings: ## Open client settings in editor
	@$${EDITOR:-nano} "$(DATA_PATH)/clientsettings.json"

test: build ## Run unit tests (separate from release build)
	dotnet test Optimum.Tests/Optimum.Tests.csproj -c Release --no-restore --verbosity quiet

bench-scaling: build ## Run the four scaling benchmarks as a regression guard
	dotnet run --project Optimum.Benchmarks -c Release -- \
		-f '*AnimBlockLOD*' '*DynamicLightCache*' '*EntityRenderDistCull*' '*ChiselLodRouting*' \
		-e json

package: build ## Build every package this host can produce (Linux/macOS/Windows)
	bash scripts/package-all.sh --version $(VERSION)

package-linux: build ## Package Linux x64 (tar.gz)
	bash scripts/package-linux.sh --version $(VERSION)

package-appimage: build ## Package Linux x64 as AppImage (single executable)
	bash scripts/package-linux.sh --format appimage --version $(VERSION)

package-macos: build ## Package macOS (.dmg/.app); ARCH=arm64 or x64
	bash scripts/package-macos.sh --arch $(or $(ARCH),arm64) --version $(VERSION)

package-win: build ## Package Windows x64 (folder + zip); needs innoextract off-Windows
	pwsh scripts/package.ps1 -Zip -Version $(VERSION)
