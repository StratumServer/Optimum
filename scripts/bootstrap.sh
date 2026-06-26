#!/usr/bin/env bash
set -euo pipefail

# Builds a clean working tree for Optimum (client fork):
#   1. Downloads the official VS client archive (this host's platform).
#   2. Decompiles closed-source DLLs (Vintagestory.dll, VintagestoryLib.dll) with ILSpy.
#   3. Clones open-source Anego forks at pinned refs.
#   4. Applies patches/ and copies sources/ on top.
#
# This only reconstructs the dev tree. To build redistributable packages, run
# scripts/package-all.ps1 (or `make package`) after `dotnet build`. Run
# scripts/check-prereqs.sh (or `make check`) to see what tools your host has.

usage() {
  cat <<'EOF'
Usage: scripts/bootstrap.sh [--version VERSION] [--client-archive PATH] [--refresh]

Options:
  --version VERSION        Vintage Story version. Default: 1.22.3
  --client-archive PATH    Existing client archive (tar.gz or zip).
  --refresh                Force re-extract, re-decompile, re-clone.
  -h, --help               Show this help.
EOF
}

version="1.22.3"
client_archive=""
refresh=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="${2:?--version requires a value}"; shift 2 ;;
    --client-archive) client_archive="${2:?--client-archive requires a value}"; shift 2 ;;
    --refresh) refresh=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

install_ilspycmd_if_missing() {
  if command -v ilspycmd >/dev/null 2>&1; then return; fi
  local dotnet_tools="$HOME/.dotnet/tools"
  if [[ -x "$dotnet_tools/ilspycmd" ]]; then
    export PATH="$dotnet_tools:$PATH"
    return
  fi
  echo "Installing ilspycmd"
  dotnet tool install -g ilspycmd >/dev/null
  export PATH="$dotnet_tools:$PATH"
}

extract_archive() {
  local archive="$1" dest="$2"
  mkdir -p "$dest"
  case "$archive" in
    *.tar.gz|*.tgz) tar -xzf "$archive" -C "$dest" ;;
    *.zip)
      if command -v unzip >/dev/null 2>&1; then
        unzip -q "$archive" -d "$dest"
      else
        python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$archive" "$dest"
      fi
      ;;
    *) echo "Unsupported archive: $archive" >&2; exit 1 ;;
  esac
}

normalize_lf() {
  find "$1" -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.json' -o -name '*.xml' -o -name '*.props' -o -name '*.targets' \) -print0 |
    while IFS= read -r -d '' file; do perl -0pi -e 's/\r\n/\n/g' "$file"; done
}

copy_tree_fresh() {
  rm -rf "$2"
  mkdir -p "$(dirname "$2")"
  cp -a "$1" "$2"
}

download_client_archive() {
  local cache_dir="$1"
  mkdir -p "$cache_dir"

  # Detect platform for download URL.
  local os_arch
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)  os_arch="linux-x64" ;;
    Linux-aarch64) os_arch="linux-arm64" ;;
    Darwin-x86_64) os_arch="osx-x64" ;;
    Darwin-arm64)  os_arch="osx-arm64" ;;
    *)             os_arch="linux-x64" ;;
  esac

  local ext="tar.gz"
  local archive_name="vs_client_${os_arch}_${version}.${ext}"
  local archive_path="$cache_dir/$archive_name"

  if [[ -f "$archive_path" ]]; then
    echo "Using cached $archive_path" >&2
    printf '%s\n' "$archive_path"
    return
  fi

  local url="https://cdn.vintagestory.at/gamefiles/stable/$archive_name"
  echo "Downloading $url" >&2
  curl -L --fail --output "$archive_path" "$url"
  printf '%s\n' "$archive_path"
}

require_cmd dotnet
require_cmd git
require_cmd perl
require_cmd python3
require_cmd curl

cd "$repo_root"

vanilla_dir="$repo_root/.vanilla"
baseline_dir="$repo_root/.baseline"
zip_cache_dir="$repo_root/.vanilla-zips"

if [[ "$refresh" == "1" ]]; then
  rm -rf "$vanilla_dir" "$baseline_dir"
fi

# 1. Download and extract client.
if [[ -z "$client_archive" ]]; then
  client_archive="$(download_client_archive "$zip_cache_dir")"
fi

if [[ ! -d "$vanilla_dir" ]]; then
  echo "Extracting $client_archive"
  extract_archive "$client_archive" "$vanilla_dir"
fi

# 2. Decompile closed-source DLLs.
install_ilspycmd_if_missing

decompile_targets=("VintagestoryLib:Vintagestory.Server+Client engine" "Vintagestory:Client executable")

for entry in "${decompile_targets[@]}"; do
  IFS=':' read -r dll_base desc <<< "$entry"
  dll_path="$(find "$vanilla_dir" -type f -name "${dll_base}.dll" -print -quit)"
  if [[ -z "$dll_path" ]]; then
    echo "Skipping $dll_base.dll (not found in archive)" >&2
    continue
  fi

  out="$baseline_dir/$dll_base"
  if [[ ! -d "$out" || "$refresh" == "1" ]]; then
    echo "Decompiling $dll_base.dll ($desc)"
    rm -rf "$out"
    mkdir -p "$out"
    ilspycmd "$dll_path" --project -o "$out" >/dev/null
    # Normalize LangVersion for .NET 10.
    find "$out" -maxdepth 1 -name '*.csproj' -exec perl -0pi -e 's#<LangVersion>15\.0</LangVersion>#<LangVersion>latest</LangVersion>#g' {} \;
  fi

  copy_tree_fresh "$out" "$repo_root/baseline/$dll_base"
done

# 3. Clone open-source forks.
forks_file="$repo_root/forks.json"
if [[ -f "$forks_file" ]]; then
  mapfile -t forks < <(
    python3 - "$forks_file" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    data = json.load(f)
for fork in data.get("forks", []):
    print(f"{fork['name']}\t{fork['url']}\t{fork['ref']}")
PY
  )

  for fork in "${forks[@]}"; do
    IFS=$'\t' read -r name url ref <<< "$fork"
    base="$baseline_dir/$name"

    if [[ ! -d "$base" || "$refresh" == "1" ]]; then
      rm -rf "$base"
      echo "Cloning $name at $ref"
      git clone --quiet "$url" "$base"
      git -C "$base" checkout --quiet "$ref"
      rm -rf "$base/.git"
      normalize_lf "$base"
    fi

    copy_tree_fresh "$base" "$repo_root/$name"
  done
fi

# 4. Apply patches.
# Patches under patches/VintagestoryLib/ and patches/Vintagestory/ target files
# inside baseline/, so they need --directory=baseline. Fork patches
# (VintagestoryApi, VSEssentials, etc.) target repo-root directories directly.
# --3way enables three-way merge, which tolerates line displacement across VS
# version bumps (falls back to normal apply when the base blob is missing).
patches_dir="$repo_root/patches"
vanilla_patch_projects="VintagestoryLib Vintagestory"
if [[ -d "$patches_dir" ]] && find "$patches_dir" -name '*.patch' -print -quit | grep -q .; then
  failed=()
  while IFS= read -r -d '' patch; do
    rel="${patch#$repo_root/}"
    echo "Applying $rel"
    apply_args=(apply --3way --whitespace=nowarn)
    top_proj="$(echo "$rel" | cut -d/ -f2)"
    if echo "$vanilla_patch_projects" | grep -qw "$top_proj"; then
      apply_args+=(--directory=baseline)
    fi
    apply_args+=("$patch")
    if ! output="$(git "${apply_args[@]}" 2>&1)"; then
      failed+=("$rel")
      echo "  FAILED: $output" | head -3
    fi
  done < <(find "$patches_dir" -type f -name '*.patch' -print0 | sort -z)

  if [[ "${#failed[@]}" -gt 0 ]]; then
    echo
    echo "${#failed[@]} patch(es) failed." >&2
    printf '  %s\n' "${failed[@]}" >&2
  fi
else
  echo "No patches/ to apply."
fi

# 5. Copy Optimum-only source files.
sources_dir="$repo_root/sources"
if [[ -d "$sources_dir" ]]; then
  while IFS= read -r -d '' src; do
    rel="${src#$sources_dir/}"
    target="$repo_root/$rel"
    mkdir -p "$(dirname "$target")"
    cp -f "$src" "$target"
  done < <(find "$sources_dir" -type f -print0)
  echo "Synced sources/ into working tree."
fi

# 6. Post-decompile fixups (csproj rewrites, ambiguity resolution).
echo "Applying post-decompile fixups..."

vanilla_lib="$repo_root/.vanilla/vintagestory/Lib"

# 6a. Rewrite VintagestoryLib.csproj with HintPaths to .vanilla DLLs.
cat > "$repo_root/baseline/VintagestoryLib/VintagestoryLib.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>VintagestoryLib</AssemblyName>
    <GenerateAssemblyInfo>False</GenerateAssemblyInfo>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>True</AllowUnsafeBlocks>
    <CheckForOverflowUnderflow>False</CheckForOverflowUnderflow>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\VintagestoryApi\VintagestoryAPI.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="cairo-sharp"><HintPath>..\..\.vanilla\vintagestory\Lib\cairo-sharp.dll</HintPath></Reference>
    <Reference Include="protobuf-net"><HintPath>..\..\.vanilla\vintagestory\Lib\protobuf-net.dll</HintPath></Reference>
    <Reference Include="Newtonsoft.Json"><HintPath>..\..\.vanilla\vintagestory\Lib\Newtonsoft.Json.dll</HintPath></Reference>
    <Reference Include="CommandLine"><HintPath>..\..\.vanilla\vintagestory\Lib\CommandLine.dll</HintPath></Reference>
    <Reference Include="SkiaSharp"><HintPath>..\..\.vanilla\vintagestory\Lib\SkiaSharp.dll</HintPath></Reference>
    <Reference Include="Open.Nat"><HintPath>..\..\.vanilla\vintagestory\Lib\Open.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Nat"><HintPath>..\..\.vanilla\vintagestory\Lib\Mono.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Cecil"><HintPath>..\..\.vanilla\vintagestory\Lib\Mono.Cecil.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis"><HintPath>..\..\.vanilla\vintagestory\Lib\Microsoft.CodeAnalysis.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis.CSharp"><HintPath>..\..\.vanilla\vintagestory\Lib\Microsoft.CodeAnalysis.CSharp.dll</HintPath></Reference>
    <Reference Include="ICSharpCode.SharpZipLib"><HintPath>..\..\.vanilla\vintagestory\Lib\ICSharpCode.SharpZipLib.dll</HintPath></Reference>
    <Reference Include="Microsoft.Data.Sqlite"><HintPath>..\..\.vanilla\vintagestory\Lib\Microsoft.Data.Sqlite.dll</HintPath></Reference>
    <Reference Include="0Harmony"><HintPath>..\..\.vanilla\vintagestory\Lib\0Harmony.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Desktop"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Windowing.Desktop.dll</HintPath></Reference>
    <Reference Include="OpenTK.Audio.OpenAL"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Audio.OpenAL.dll</HintPath></Reference>
    <Reference Include="OpenTK.Mathematics"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Mathematics.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Common"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Windowing.Common.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.GraphicsLibraryFramework"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Windowing.GraphicsLibraryFramework.dll</HintPath></Reference>
    <Reference Include="DnsClient"><HintPath>..\..\.vanilla\vintagestory\Lib\DnsClient.dll</HintPath></Reference>
    <Reference Include="OpenTK.Graphics"><HintPath>..\..\.vanilla\vintagestory\Lib\OpenTK.Graphics.dll</HintPath></Reference>
    <Reference Include="csvorbis"><HintPath>..\..\.vanilla\vintagestory\Lib\csvorbis.dll</HintPath></Reference>
    <Reference Include="csogg"><HintPath>..\..\.vanilla\vintagestory\Lib\csogg.dll</HintPath></Reference>
    <Reference Include="xplatforminterface"><HintPath>..\..\.vanilla\vintagestory\Lib\xplatforminterface.dll</HintPath></Reference>
  </ItemGroup>
</Project>
CSPROJ

# 6b. Fix VSEssentials: Tavis.JsonPatch PackageReference → local DLL Reference.
perl -pi -e 's|<PackageReference Include="Tavis.JsonPatch" Version="[^"]*" />|<Reference Include="Tavis.JsonPatch"><HintPath>..\\.vanilla\\vintagestory\\Lib\\Tavis.JsonPatch.dll</HintPath><Private>false</Private></Reference>|' \
  "$repo_root/VSEssentials/VSEssentialsMod.csproj" \
  "$repo_root/VSSurvivalMod/VSSurvivalMod.csproj" 2>/dev/null

# 6c. Fix Mapping ambiguity in ServerSystemUpnp.
upnp="$repo_root/baseline/VintagestoryLib/Vintagestory.Server/ServerSystemUpnp.cs"
if [[ -f "$upnp" ]]; then
  perl -pi -e '
    s/^\tprivate Mapping mapping;/\tprivate Open.Nat.Mapping mapping;/;
    s/^\tprivate Mapping mappingUdp;/\tprivate Open.Nat.Mapping mappingUdp;/;
    s/^\tprivate Mapping monoNatMapping;/\tprivate Mono.Nat.Mapping monoNatMapping;/;
    s/^\tprivate Mapping monoNatMappingUdp;/\tprivate Mono.Nat.Mapping monoNatMappingUdp;/;
  ' "$upnp"
  perl -pi -e '
    s/mapping = new Mapping\(\(Protocol\)0/mapping = new Open.Nat.Mapping((Open.Nat.Protocol)0/;
    s/mappingUdp = new Mapping\(\(Protocol\)1/mappingUdp = new Open.Nat.Mapping((Open.Nat.Protocol)1/;
    s/monoNatMapping = new Mapping\(\(Protocol\)0/monoNatMapping = new Mono.Nat.Mapping((Mono.Nat.Protocol)0/;
    s/monoNatMappingUdp = new Mapping\(\(Protocol\)1/monoNatMappingUdp = new Mono.Nat.Mapping((Mono.Nat.Protocol)1/;
  ' "$upnp"
fi

# 6d. Fix ModContainer CustomAttributeNamedArgument ambiguity.
modcont="$repo_root/baseline/VintagestoryLib/Vintagestory.Common/ModContainer.cs"
if [[ -f "$modcont" ]]; then
  perl -pi -e 's/(?<!\.)(?<!Mono\.Cecil\.)(?<!System\.Reflection\.)CustomAttributeNamedArgument(?!.*namespace)/Mono.Cecil.CustomAttributeNamedArgument/g unless /^using/' "$modcont"
fi

echo "Post-decompile fixups done."

# 6e. Final pass: catch remaining decompiler artifacts (ref-casts, op_Implicit,
#     ambiguous types, GeneratedRegex) across the entire baseline.
find "$repo_root/baseline" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/\(\(([\w.]+)\)\(ref (\w+)\)\)\._002Ector\(/$2 = new $1(/g;
  s/\(\(([\w.]+)\)\(ref ([\w.]+)\)\)/$2/g;
  s/\([^)]*<[^>]+>\)\(ref (\w+)\)/$1/g;
  s/JToken\.op_Implicit\((.+?)\)/(JToken)($1)/g;
'
# Fix GeneratedRegex: replace decompiled source-generator stubs with new Regex().
find "$repo_root/baseline" -name '*.cs' -print0 | xargs -0 perl -0pi -e '
  s/\t\[GeneratedRegex\("([^"]+)"\)\]\n\t\[GeneratedCode\([^\]]+\)\]\n\tprivate static Regex (\w+)\(\)\n\t\{\n\t\treturn [^;]+;\n\t\}/\tprivate static Regex $2()\n\t{\n\t\treturn new Regex("$1", RegexOptions.Compiled);\n\t}/g;
'
# Fix MouseWheelEventArgs ambiguity (OpenTK vs Vintagestory.API.Client).
cpw="$repo_root/baseline/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs"
if [[ -f "$cpw" ]]; then
  perl -pi -e '
    s/private void Mouse_WheelChanged\(MouseWheelEventArgs e\)/private void Mouse_WheelChanged(OpenTK.Windowing.Common.MouseWheelEventArgs e)/;
    s/MouseWheelEventArgs e2 = new MouseWheelEventArgs/Vintagestory.API.Client.MouseWheelEventArgs e2 = new Vintagestory.API.Client.MouseWheelEventArgs/;
  ' "$cpw"
fi

# 7. Copy Optimum-only source files.
sources_dir="$repo_root/sources"
if [[ -d "$sources_dir" ]]; then
  while IFS= read -r -d '' src; do
    rel="${src#$sources_dir/}"
    target="$repo_root/$rel"
    mkdir -p "$(dirname "$target")"
    cp -f "$src" "$target"
  done < <(find "$sources_dir" -type f -print0)
  echo "Synced sources/ into working tree."
fi

echo
echo "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release"
