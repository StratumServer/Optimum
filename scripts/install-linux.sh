#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"

xdg_data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_dir="$xdg_data_home/optimum"
data_path=""
package_dir=""
create_menu=1
create_desktop=0
skip_build=0
version=""

usage() {
  printf '%s\n' \
    "Usage: $0 [options]" \
    "" \
    "Options:" \
    "  --install-dir DIR       Install Optimum to DIR." \
    "  --data-path DIR         Pass --dataPath DIR when Optimum starts." \
    "  --package-dir DIR       Install an existing Optimum Linux package folder." \
    "  --skip-build            Package existing build outputs without bootstrap or build." \
    "  --version VERSION       Vintage Story version for package-linux.ps1." \
    "  --no-menu-entry         Do not create the application menu entry." \
    "  --desktop-shortcut      Create the Desktop shortcut." \
    "  --help                  Show this help."
}

die() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

log() {
  printf '==> %s\n' "$*" >&2
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Missing required command: $1"
}

need_arg() {
  [[ $# -gt 1 ]] || die "$1 needs a value"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-dir)
      need_arg "$@"
      install_dir="$2"
      shift 2
      ;;
    --data-path)
      need_arg "$@"
      data_path="$2"
      shift 2
      ;;
    --package-dir)
      need_arg "$@"
      package_dir="$2"
      shift 2
      ;;
    --skip-build)
      skip_build=1
      shift
      ;;
    --version)
      need_arg "$@"
      version="$2"
      shift 2
      ;;
    --no-menu-entry)
      create_menu=0
      shift
      ;;
    --desktop-shortcut)
      create_desktop=1
      shift
      ;;
    --no-desktop-shortcut)
      create_desktop=0
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      die "Unknown option: $1"
      ;;
  esac
done

abs_path() {
  local path="$1"
  if [[ "$path" == /* ]]; then
    printf '%s\n' "$path"
  else
    printf '%s\n' "$PWD/${path#./}"
  fi
}

install_dir="$(abs_path "$install_dir")"
if [[ -n "$data_path" ]]; then
  data_path="$(abs_path "$data_path")"
fi
if [[ -n "$package_dir" ]]; then
  package_dir="$(abs_path "$package_dir")"
fi

guard_install_dir() {
  local dir="$1"
  local home_dir
  local data_dir
  home_dir="$(abs_path "$HOME")"
  data_dir="$(abs_path "$xdg_data_home")"

  case "$dir" in
    ""|"/"|"$home_dir"|"$data_dir"|"$home_dir/.local")
      die "Refusing unsafe install dir: $dir"
      ;;
  esac
}

check_dotnet10() {
  dotnet --list-sdks 2>/dev/null | grep -q '^10\.'
}

desktop_dir() {
  local found=""
  if command -v xdg-user-dir >/dev/null 2>&1; then
    found="$(xdg-user-dir DESKTOP 2>/dev/null || true)"
  fi
  if [[ -z "$found" || "$found" == "$HOME" ]]; then
    found="$HOME/Desktop"
  fi
  printf '%s\n' "$found"
}

desktop_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

write_launcher() {
  local launcher="$install_dir/optimum-launch.sh"
  if [[ -n "$data_path" ]]; then
    local quoted_data_path
    printf -v quoted_data_path '%q' "$data_path"
    mkdir -p "$data_path"
    cat > "$launcher" <<EOF
#!/usr/bin/env bash
set -euo pipefail
cd "\$(dirname "\${BASH_SOURCE[0]}")"
exec ./run.sh --dataPath $quoted_data_path "\$@"
EOF
  else
    cat > "$launcher" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
exec ./run.sh "$@"
EOF
  fi
  chmod +x "$launcher"
}

write_desktop_entry() {
  local target="$1"
  local launcher="$install_dir/optimum-launch.sh"
  local escaped_launcher
  local escaped_install_dir
  escaped_launcher="$(desktop_escape "$launcher")"
  escaped_install_dir="$(desktop_escape "$install_dir")"

  mkdir -p "$(dirname "$target")"
  cat > "$target" <<EOF
[Desktop Entry]
Type=Application
Name=Optimum
Comment=High-performance client for Vintage Story
Exec="$escaped_launcher"
Path=$escaped_install_dir
Icon=optimum
Terminal=false
Categories=Game;
StartupWMClass=Optimum
EOF
  chmod +x "$target"
}

install_package_dir() {
  local source_dir="$1"
  local source_real
  local target_real
  [[ -d "$source_dir" ]] || die "Package dir not found: $source_dir"
  [[ -f "$source_dir/run.sh" ]] || die "Package dir lacks run.sh: $source_dir"

  guard_install_dir "$install_dir"
  source_real="$(realpath -m "$source_dir")"
  target_real="$(realpath -m "$install_dir")"
  [[ "$source_real" != "$target_real" ]] || die "Package dir and install dir must differ"

  log "Installing Optimum to $install_dir"
  rm -rf "$install_dir"
  mkdir -p "$install_dir"
  cp -a "$source_dir"/. "$install_dir"/

  if [[ -f "$install_dir/Optimum" ]]; then
    chmod +x "$install_dir/Optimum"
  fi
  chmod +x "$install_dir/run.sh"
}

build_package() {
  require_cmd tar
  require_cmd pwsh

  cd "$repo_root"
  if [[ "$skip_build" -eq 0 ]]; then
    require_cmd make
    require_cmd dotnet
    require_cmd git
    require_cmd curl
    require_cmd python3
    require_cmd perl
    check_dotnet10 || die ".NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download"

    log "Building Optimum"
    if [[ -n "$version" ]]; then
      make -C "$repo_root" VERSION="$version" build >&2
    else
      make -C "$repo_root" build >&2
    fi
  fi

  local stage_root
  local package_args
  stage_root="$(mktemp -d)"
  package_args=("$repo_root/scripts/package-linux.ps1" -OutputDir "$stage_root")
  if [[ -n "$version" ]]; then
    package_args+=(-Version "$version")
  fi

  log "Packaging Linux build"
  pwsh -NoProfile -File "${package_args[@]}" >&2

  local built_dir
  built_dir="$(find "$stage_root" -maxdepth 1 -type d -name 'Optimum-v*-linux-x64' | sort | tail -n 1)"
  [[ -n "$built_dir" ]] || die "Linux package folder not found under $stage_root"
  printf '%s\n' "$built_dir"
}

refresh_desktop_databases() {
  local apps_dir="$xdg_data_home/applications"
  local icon_root="$xdg_data_home/icons/hicolor"

  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$apps_dir" >/dev/null 2>&1 || true
  fi
  if command -v gtk-update-icon-cache >/dev/null 2>&1 && [[ -d "$icon_root" ]]; then
    gtk-update-icon-cache -q "$icon_root" >/dev/null 2>&1 || true
  fi
}

source_dir="$package_dir"
temp_source=""
if [[ -z "$source_dir" ]]; then
  temp_source="$(build_package)"
  source_dir="$temp_source"
fi

install_package_dir "$source_dir"
write_launcher
if [[ -n "$temp_source" ]]; then
  rm -rf "$(dirname "$temp_source")"
fi

icon_dir="$xdg_data_home/icons/hicolor/256x256/apps"
mkdir -p "$icon_dir"
if [[ -f "$install_dir/assets/gameicon.png" ]]; then
  cp -f "$install_dir/assets/gameicon.png" "$icon_dir/optimum.png"
elif [[ -f "$repo_root/docs/logo.png" ]]; then
  cp -f "$repo_root/docs/logo.png" "$icon_dir/optimum.png"
fi

if [[ "$create_menu" -eq 1 ]]; then
  write_desktop_entry "$xdg_data_home/applications/optimum.desktop"
fi

if [[ "$create_desktop" -eq 1 ]]; then
  write_desktop_entry "$(desktop_dir)/Optimum.desktop"
fi

refresh_desktop_databases

log "Optimum installed"
printf 'App: %s\n' "$install_dir"
if [[ "$create_menu" -eq 1 ]]; then
  printf 'Menu: %s\n' "$xdg_data_home/applications/optimum.desktop"
fi
if [[ "$create_desktop" -eq 1 ]]; then
  printf 'Desktop: %s\n' "$(desktop_dir)/Optimum.desktop"
fi
printf 'Run: %s/optimum-launch.sh\n' "$install_dir"
