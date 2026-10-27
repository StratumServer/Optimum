#!/usr/bin/env bash
# Verifies the Linux installer stages the package without building an archive
# it never uses. Regression cover for the issue #23 follow-up: an install onto
# /mnt/zoomin stalled while package-linux.sh built a throwaway tar.gz in a
# small /tmp, after the staged folder was already complete.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

# --- package-linux.sh --format validation ------------------------------------

if bash "$REPO_ROOT/scripts/package-linux.sh" --format bogus >/dev/null 2>"$TEST_ROOT/fmt-err"; then
    echo 'package-linux.sh accepted an invalid --format' >&2
    exit 1
fi
grep -Fq "targz" "$TEST_ROOT/fmt-err"
grep -Fq "none" "$TEST_ROOT/fmt-err"

# The 'none' arm must sit before the catch-all so it does not fall through to
# the tar.gz default.
none_line="$(grep -n '^\s*none)' "$REPO_ROOT/scripts/package-linux.sh" | head -1 | cut -d: -f1)"
star_line="$(grep -n '^\s*\*)' "$REPO_ROOT/scripts/package-linux.sh" | tail -1 | cut -d: -f1)"
[[ -n "$none_line" && -n "$star_line" && "$none_line" -lt "$star_line" ]]

# --- install-linux.sh / install-linux-legacy.sh cleanup ---------------------

for installer in install-linux.sh install-linux-legacy.sh; do
    ( # subshell: sourcing runs the file's top level, not main
        trap - EXIT  # drop the inherited cleanup trap; test only what sourcing adds
        # shellcheck disable=SC1090
        source "$REPO_ROOT/scripts/$installer"

        # cleanup_stage is a no-op and succeeds when nothing was staged
        # (set -e above fails the test if it returns non-zero).
        STAGE_ROOT=""
        cleanup_stage

        # It removes the staged tree when one exists.
        STAGE_ROOT="$TEST_ROOT/stage-$installer"
        mkdir -p "$STAGE_ROOT/inner"
        cleanup_stage
        [[ ! -e "$STAGE_ROOT" ]]

        # Sourcing must not install an EXIT trap (that would clobber the trap
        # of any script, such as the prerequisite test, that sources this one).
        [[ -z "$(trap -p EXIT)" ]]

        # The installer asks package-linux.sh for the folder only.
        grep -Fq -- '--format none' "$REPO_ROOT/scripts/$installer"
    )
done

# --- ModDB URI handler, .desktop registration, spaces, and XDG coexistence ---

for installer in install-linux.sh install-linux-legacy.sh; do
    SANDBOX="$TEST_ROOT/uri-$installer"
    FAKE_HOME="$SANDBOX/home"
    XDG_DATA="$FAKE_HOME/.local/share"
    XDG_CFG="$FAKE_HOME/.config"
    PKG_DIR="$SANDBOX/staged/Optimum-v0.3.21-linux-x64"
    INSTALL_SPACES="$SANDBOX/Install Dir With Spaces/Optimum Client"
    DATA_SPACES="$SANDBOX/Custom Data Path/VintageStory Data"

    mkdir -p "$XDG_DATA/applications" "$XDG_CFG" "$PKG_DIR/assets" "$PKG_DIR/.optimum"
    cat > "$PKG_DIR/run.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
for arg in "$@"; do
    printf '<%s>' "$arg"
done
printf '\n'
EOF
    chmod +x "$PKG_DIR/run.sh"
    printf '#!/usr/bin/env bash\nexit 0\n' > "$PKG_DIR/Optimum"
    chmod +x "$PKG_DIR/Optimum"
    printf 'PNG' > "$PKG_DIR/assets/gameicon.png"

    # Pre-seed a lowercase foreign URI handler to verify update-desktop-database
    # cannot silently steal the default association when --set-default-uri-handler is omitted.
    cat > "$XDG_DATA/applications/vintagestory_url_mod.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Vintage Story URI Handler
Exec="/tmp/vs" -i %u
MimeType=x-scheme-handler/vintagestorymodinstall;
EOF
    cat > "$XDG_CFG/mimeapps.list" <<'EOF'
[Default Applications]
x-scheme-handler/vintagestorymodinstall=vintagestory_url_mod.desktop
EOF

    # 1. Install without --set-default-uri-handler: must keep existing default handler.
    HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" \
        bash "$REPO_ROOT/scripts/$installer" \
        --non-interactive \
        --package-dir "$PKG_DIR" \
        --install-dir "$INSTALL_SPACES" \
        --data-path "$DATA_SPACES" >/dev/null

    DESKTOP_FILE="$XDG_DATA/applications/optimum.desktop"
    [[ -f "$DESKTOP_FILE" ]]
    grep -Fq "Exec=\"$INSTALL_SPACES/optimum-launch.sh\" %u" "$DESKTOP_FILE"
    grep -Fq "MimeType=x-scheme-handler/vintagestorymodinstall;" "$DESKTOP_FILE"
    grep -Fq "x-scheme-handler/vintagestorymodinstall=vintagestory_url_mod.desktop" "$XDG_CFG/mimeapps.list"
    if command -v xdg-mime >/dev/null 2>&1; then
        [[ "$(HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" xdg-mime query default x-scheme-handler/vintagestorymodinstall)" == "vintagestory_url_mod.desktop" ]]
    fi

    # 2. Reinstall with --set-default-uri-handler: explicitly sets optimum.desktop and records previous handler.
    HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" \
        bash "$REPO_ROOT/scripts/$installer" \
        --non-interactive \
        --package-dir "$PKG_DIR" \
        --install-dir "$INSTALL_SPACES" \
        --data-path "$DATA_SPACES" \
        --set-default-uri-handler >/dev/null

    grep -Fq "x-scheme-handler/vintagestorymodinstall=optimum.desktop" "$XDG_CFG/mimeapps.list"
    if command -v xdg-mime >/dev/null 2>&1; then
        [[ "$(HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" xdg-mime query default x-scheme-handler/vintagestorymodinstall)" == "optimum.desktop" ]]
    fi

    # 3. Verify optimum-launch.sh argument construction and URI validation.
    LAUNCHER="$INSTALL_SPACES/optimum-launch.sh"
    [[ "$("$LAUNCHER" "vintagestorymodinstall://carrycapacity@1.8.0")" == "<--dataPath><$DATA_SPACES><-i><vintagestorymodinstall://carrycapacity@1.8.0>" ]]
    [[ "$("$LAUNCHER" "VINTAGESTORYMODINSTALL://primitivesurvival@3.7.5/")" == "<--dataPath><$DATA_SPACES><-i><vintagestorymodinstall://primitivesurvival@3.7.5>" ]]
    [[ "$("$LAUNCHER" --openWorld "My World" -p creativebuilding)" == "<--dataPath><$DATA_SPACES><--openWorld><My World><-p><creativebuilding>" ]]

    for bad_uri in \
        "http://mods.vintagestory.at/carrycapacity" \
        "vintagestorymodinstall://" \
        "vintagestorymodinstall:///" \
        "vintagestorymodinstall://-c" \
        $'vintagestorymodinstall://carrycapacity@1.8.0\n-c evil' \
        $'vintagestorymodinstall://carrycapacity@1.8.0\r-c evil' \
        "vintagestorymodinstall://carrycapacity@1.8.0 --connect evil" \
        "vintagestorymodinstall://\$(touch '$SANDBOX/pwned')"; do
        if "$LAUNCHER" "$bad_uri" >"$SANDBOX/bad-out" 2>/dev/null; then
            echo "Launcher accepted malformed URI: $bad_uri" >&2
            exit 1
        fi
        [[ ! -s "$SANDBOX/bad-out" ]]
    done
    [[ ! -e "$SANDBOX/pwned" ]]

    # 4. Reinstall with --no-default-uri-handler restores vintagestory_url_mod.desktop.
    HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" \
        bash "$REPO_ROOT/scripts/$installer" \
        --non-interactive \
        --package-dir "$PKG_DIR" \
        --install-dir "$INSTALL_SPACES" \
        --data-path "$DATA_SPACES" \
        --no-default-uri-handler >/dev/null

    grep -Fq "x-scheme-handler/vintagestorymodinstall=vintagestory_url_mod.desktop" "$XDG_CFG/mimeapps.list"
    if command -v xdg-mime >/dev/null 2>&1; then
        [[ "$(HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" xdg-mime query default x-scheme-handler/vintagestorymodinstall)" == "vintagestory_url_mod.desktop" ]]
    fi

    # 5. Reinstall with --set-default-uri-handler then uninstall via scripts/uninstall.sh:
    #    removes optimum.desktop and leaves no stale optimum.desktop association.
    HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" \
        bash "$REPO_ROOT/scripts/$installer" \
        --non-interactive \
        --package-dir "$PKG_DIR" \
        --install-dir "$INSTALL_SPACES" \
        --data-path "$DATA_SPACES" \
        --set-default-uri-handler >/dev/null

    HOME="$FAKE_HOME" XDG_DATA_HOME="$XDG_DATA" XDG_CONFIG_HOME="$XDG_CFG" \
        bash "$REPO_ROOT/scripts/uninstall.sh" \
        --install-dir "$INSTALL_SPACES" \
        --yes >/dev/null

    [[ ! -e "$INSTALL_SPACES" ]]
    [[ ! -e "$DESKTOP_FILE" ]]
    ! grep -Fq "optimum.desktop" "$XDG_CFG/mimeapps.list"
    if [[ -f "$XDG_DATA/applications/mimeinfo.cache" ]]; then
        ! grep -Fq "optimum.desktop" "$XDG_DATA/applications/mimeinfo.cache"
    fi
done

echo 'Linux installer staging test passed.'
