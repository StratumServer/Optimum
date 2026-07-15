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

pinned_ilspycmd_version() {
  local manifest="$repo_root/.config/dotnet-tools.json"
  [[ -f "$manifest" ]] || return 0
  python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['tools']['ilspycmd']['version'])" "$manifest"
}

ilspycmd_accepted_prefixes() {
  local manifest="$repo_root/.config/ilspycmd-compat.json"
  if [[ -f "$manifest" ]]; then
    grep -oE '"[0-9]+\.[0-9]+\.[0-9]+\."' "$manifest" | tr -d '"'
    return
  fi
  printf '%s\n' '10.1.0.' '10.1.1.'
}

ilspycmd_version_supported() {
  local current="$1" prefix
  while IFS= read -r prefix; do
    [[ -n "$prefix" && "$current" == "$prefix"* ]] && return 0
  done < <(ilspycmd_accepted_prefixes)
  return 1
}

install_ilspycmd_if_missing() {
  local dotnet_tools="$HOME/.dotnet/tools"
  if [[ -x "$dotnet_tools/ilspycmd" ]]; then
    export PATH="$dotnet_tools:$PATH"
  fi

  local pinned
  pinned="$(pinned_ilspycmd_version)"

  if command -v ilspycmd >/dev/null 2>&1; then
    if [[ -z "$pinned" ]]; then return; fi
    local current
    current="$(ilspycmd --version 2>/dev/null | head -1 | awk '{print $2}')"
    if ilspycmd_version_supported "$current"; then return; fi
    echo "ilspycmd $current is unsupported by this patch set, installing $pinned" >&2
    dotnet tool uninstall -g ilspycmd >/dev/null 2>&1 || true
  fi

  if [[ -n "$pinned" ]]; then
    echo "Installing ilspycmd $pinned"
    dotnet tool install -g ilspycmd --version "$pinned" >/dev/null
  else
    echo "Installing ilspycmd (latest)"
    dotnet tool install -g ilspycmd >/dev/null
  fi
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

git_apply_optimum_patch() {
  local patch="$1"
  local dir_arg="${2:-}"
  local args=(--whitespace=nowarn)
  local output

  if [[ -n "$dir_arg" ]]; then
    args+=("$dir_arg")
  fi

  if output="$(git apply "${args[@]}" "$patch" 2>&1)"; then
    return 0
  fi

  local first_output="$output"
  if output="$(git apply "${args[@]}" -p0 "$patch" 2>&1)"; then
    echo "applied with -p0"
    return 0
  fi

  if [[ -n "$dir_arg" ]] && output="$(git apply --whitespace=nowarn -p0 "$patch" 2>&1)"; then
    echo "applied with root -p0"
    return 0
  fi

  printf '%s\n' "$first_output"
  return 1
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

# Vanilla client files always live under .vanilla/win-x64/, matching the
# convention used by bootstrap.ps1, install-windows.ps1, package.ps1, and
# _hostcaps.ps1. VintagestoryLib.csproj's HintPaths hardcode this path.
# The Lib/ and game DLLs are identical bytes across platform archives (pure
# .NET IL), so extracting any platform's archive here is correct regardless
# of host OS: only the native launcher differs, and that lives outside this
# tree in package-time cross-builds, not here.
vanilla_dir="$repo_root/.vanilla/win-x64"
snapshot_dir="$repo_root/build/snapshot"
zip_cache_dir="$repo_root/.vanilla/archives"
sources_dir="$repo_root/sources"

if [[ "$refresh" == "1" ]]; then
  rm -rf "$vanilla_dir" "$snapshot_dir"
fi

# Invalidate the completion stamp for the duration of this run; the end of the
# script recreates it. See the note next to the touch at the bottom.
rm -f "$repo_root/.bootstrap-complete"

# 1. Download and extract client.
if [[ -z "$client_archive" ]]; then
  client_archive="$(download_client_archive "$zip_cache_dir")"
fi

# `make deploy` overwrites $vanilla_dir/vintagestory/VintagestoryLib.dll with
# the Cecil-patched output, since that directory doubles as the client folder
# used for testing (`make run`). Optimum.Patcher's own vanilla input reads
# from a separate, protected copy made once right after a fresh extraction,
# when the file is guaranteed clean, so a second `patch-il` run after a
# `deploy` never transplants onto its own prior output instead of true
# vanilla.
vanilla_lib_pristine="$vanilla_dir/vintagestory/VintagestoryLib.vanilla.dll"

if [[ ! -d "$vanilla_dir/vintagestory" ]]; then
  echo "Extracting $client_archive"
  extract_archive "$client_archive" "$vanilla_dir"
  cp "$vanilla_dir/vintagestory/VintagestoryLib.dll" "$vanilla_lib_pristine"
elif [[ ! -f "$vanilla_lib_pristine" ]]; then
  echo "WARNING: $vanilla_lib_pristine is missing." >&2
  echo "  If \$vanilla_dir/vintagestory/VintagestoryLib.dll has ever been" >&2
  echo "  overwritten by 'make deploy', it may no longer be pristine vanilla." >&2
  echo "  Re-run with --refresh, or restore VintagestoryLib.vanilla.dll from" >&2
  echo "  \$zip_cache_dir manually before running 'make patch-il'." >&2
fi

# 2. Decompile closed-source DLLs.
install_ilspycmd_if_missing

# 2. Decompile all closed-source DLLs.
# Produces .csproj projects in build/snapshot/{name}/ copied to working dirs.
decompile_targets=("VintagestoryLib:build/VintagestoryLib" "Vintagestory:build/Vintagestory")

for entry in "${decompile_targets[@]}"; do
  IFS=':' read -r dll_base work_dir <<< "$entry"
  dll_path="$(find "$vanilla_dir" -type f -name "${dll_base}.dll" -print -quit)"
  if [[ -z "$dll_path" ]]; then
    echo "Skipping $dll_base.dll (not found)" >&2
    continue
  fi

  out="$snapshot_dir/$dll_base"
  if [[ ! -d "$out" || "$refresh" == "1" ]]; then
    echo "Decompiling $dll_base.dll with $(ilspycmd --version | head -1)"
    rm -rf "$out"
    mkdir -p "$out"
    ilspycmd "$dll_path" --project -o "$out" >/dev/null
    find "$out" -maxdepth 1 -name '*.csproj' -exec perl -0pi -e 's#<LangVersion>15\.0</LangVersion>#<LangVersion>latest</LangVersion>#g' {} \;
  fi

  copy_tree_fresh "$out" "$repo_root/$work_dir"
done

# 3. Clone compile-target forks (VintagestoryApi, Cairo).
forks_file="$repo_root/forks.json"
if [[ -f "$forks_file" ]]; then
  while IFS=$'\t' read -r name url ref; do
    base="$snapshot_dir/$name"

    if [[ ! -d "$base" || "$refresh" == "1" ]]; then
      rm -rf "$base"
      echo "Cloning $name at $ref"
      git clone --quiet "$url" "$base"
      git -C "$base" checkout --quiet "$ref"
      rm -rf "$base/.git"
      normalize_lf "$base"
    fi

    copy_tree_fresh "$base" "$repo_root/$name"
  done < <(
    python3 - "$forks_file" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    data = json.load(f)
for fork in data.get("compile", []):
    if fork.get("source") == "clone":
        print(f"{fork['name']}\t{fork['url']}\t{fork['ref']}")
PY
  )
fi

# 3b. Clone reference repos (for code reading, not compilation).
ref_dir="$repo_root/ref/source"
if [[ -f "$forks_file" ]]; then
  while IFS=$'\t' read -r name url ref; do
    dest="$ref_dir/$name"
    if [[ ! -d "$dest" ]]; then
      echo "Cloning reference: $name"
      git clone --quiet --depth=1 "$url" "$dest" 2>/dev/null
      git -C "$dest" checkout --quiet "$ref" 2>/dev/null
    fi
  done < <(
    python3 - "$forks_file" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    data = json.load(f)
for r in data.get("reference", []):
    print(f"{r['name']}\t{r['url']}\t{r['ref']}")
PY
  )
fi

# (Steps 4+5 moved after fixups. Patches apply on top of the post-fixup baseline.)

# 6. Post-decompile fixups (csproj rewrites, ambiguity resolution).
echo "Applying post-decompile fixups..."

# Normalize CRLF across all decompiled .cs files FIRST (ilspycmd on Windows emits CRLF).
# Anchored fixup patterns fail on lines that end with \r, so this must run before any fixup.
# perl -pi instead of sed -i: macOS BSD sed reads GNU-style -i arguments as the backup
# suffix and then parses the target path as its script, which aborts the bootstrap.
find "$repo_root/build" -name '*.cs' -print0 | xargs -0 perl -pi -e 's/\r$//'

vanilla_lib="$repo_root/.vanilla/win-x64/vintagestory/Lib"

# 6a. Rewrite VintagestoryLib.csproj with HintPaths to .vanilla DLLs.
cat > "$repo_root/build/VintagestoryLib/VintagestoryLib.csproj" <<'CSPROJ'
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
    <Reference Include="cairo-sharp"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\cairo-sharp.dll</HintPath></Reference>
    <Reference Include="protobuf-net"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\protobuf-net.dll</HintPath></Reference>
    <Reference Include="Newtonsoft.Json"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Newtonsoft.Json.dll</HintPath></Reference>
    <Reference Include="CommandLine"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\CommandLine.dll</HintPath></Reference>
    <Reference Include="SkiaSharp"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\SkiaSharp.dll</HintPath></Reference>
    <Reference Include="Open.Nat"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Open.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Nat"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Mono.Nat.dll</HintPath></Reference>
    <Reference Include="Mono.Cecil"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Mono.Cecil.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Microsoft.CodeAnalysis.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis.CSharp"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Microsoft.CodeAnalysis.CSharp.dll</HintPath></Reference>
    <Reference Include="ICSharpCode.SharpZipLib"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\ICSharpCode.SharpZipLib.dll</HintPath></Reference>
    <Reference Include="Microsoft.Data.Sqlite"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\Microsoft.Data.Sqlite.dll</HintPath></Reference>
    <Reference Include="0Harmony"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\0Harmony.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Desktop"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Windowing.Desktop.dll</HintPath></Reference>
    <Reference Include="OpenTK.Audio.OpenAL"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Audio.OpenAL.dll</HintPath></Reference>
    <Reference Include="OpenTK.Mathematics"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Mathematics.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.Common"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Windowing.Common.dll</HintPath></Reference>
    <Reference Include="OpenTK.Windowing.GraphicsLibraryFramework"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Windowing.GraphicsLibraryFramework.dll</HintPath></Reference>
    <Reference Include="DnsClient"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\DnsClient.dll</HintPath></Reference>
    <Reference Include="OpenTK.Graphics"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\OpenTK.Graphics.dll</HintPath></Reference>
    <Reference Include="csvorbis"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\csvorbis.dll</HintPath></Reference>
    <Reference Include="csogg"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\csogg.dll</HintPath></Reference>
    <Reference Include="xplatforminterface"><HintPath>..\..\.vanilla\win-x64\vintagestory\Lib\xplatforminterface.dll</HintPath></Reference>
  </ItemGroup>
</Project>
CSPROJ

# 6b. Fix VSEssentials: Tavis.JsonPatch PackageReference → local DLL Reference.
perl -pi -e 's|<PackageReference Include="Tavis.JsonPatch" Version="[^"]*" />|<Reference Include="Tavis.JsonPatch"><HintPath>..\\.vanilla\\win-x64\\vintagestory\\Lib\\Tavis.JsonPatch.dll</HintPath><Private>false</Private></Reference>|' \
  "$repo_root/VSEssentials/VSEssentialsMod.csproj" \
  "$repo_root/VSSurvivalMod/VSSurvivalMod.csproj" 2>/dev/null

# 6c. Fix Mapping ambiguity in ServerSystemUpnp.
upnp="$repo_root/build/VintagestoryLib/Vintagestory.Server/ServerSystemUpnp.cs"
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
modcont="$repo_root/build/VintagestoryLib/Vintagestory.Common/ModContainer.cs"
if [[ -f "$modcont" ]]; then
  perl -pi -e 's/(?<!\.)(?<!Mono\.Cecil\.)(?<!System\.Reflection\.)CustomAttributeNamedArgument(?!.*namespace)/Mono.Cecil.CustomAttributeNamedArgument/g unless /^using/' "$modcont"
fi

echo "Post-decompile fixups done."

# 6e. Final pass: catch remaining decompiler artifacts (ref-casts, op_Implicit,
#     ambiguous types, GeneratedRegex) across the entire build tree.
find "$repo_root/build" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/\(\(([\w.]+)\)\(ref (\w+)\)\)\._002Ector\(/$2 = new $1(/g;
  s/\(\(([\w.]+)\)\(ref ([\w.]+)\)\)/$2/g;
  s/\([^)]*<[^>]+>\)\(ref (\w+)\)/$1/g;
  s/JToken\.op_Implicit\((.+?)\)/(JToken)($1)/g;
'
# Fix GeneratedRegex: replace decompiled source-generator stubs with new Regex().
find "$repo_root/build" -name '*.cs' -print0 | xargs -0 perl -0pi -e '
  s/\t\[GeneratedRegex\("([^"]+)"\)\]\n\t\[GeneratedCode\([^\]]+\)\]\n\tprivate static Regex (\w+)\(\)\n\t\{\n\t\treturn [^;]+;\n\t\}/\tprivate static Regex $2()\n\t{\n\t\treturn new Regex("$1", RegexOptions.Compiled);\n\t}/g;
'
# The fixup above bypasses every [GeneratedRegex] stub, so the source-generated
# regex-matching implementation classes ILSpy decompiles alongside them
# (Vintagestory.dll/VintagestoryLib.dll's `System.Text.RegularExpressions.Generated`
# namespace) are now 100% dead code, never referenced from anywhere. They carry
# their own decompiler artifacts (RuntimeHelpers, protected-member access,
# char/ushort* casts); deleting them is simpler and safer than fixing artifacts in
# code nothing calls.
find "$repo_root/build" -type d -name 'System.Text.RegularExpressions.Generated' -exec rm -rf {} +
# Fix MouseWheelEventArgs ambiguity (OpenTK vs Vintagestory.API.Client).
cpw="$repo_root/build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs"
if [[ -f "$cpw" ]]; then
  perl -pi -e '
    s/private void Mouse_WheelChanged\(MouseWheelEventArgs e\)/private void Mouse_WheelChanged(OpenTK.Windowing.Common.MouseWheelEventArgs e)/;
    s/MouseWheelEventArgs e2 = new MouseWheelEventArgs/Vintagestory.API.Client.MouseWheelEventArgs e2 = new Vintagestory.API.Client.MouseWheelEventArgs/;
  ' "$cpw"
fi

# 6g. Additional ILSpy artifacts not covered by the generic regex passes.
lib="$repo_root/build/VintagestoryLib"

# Path ambiguity (Cairo.Path vs System.IO.Path) in GUI screens.
for f in "$lib"/Vintagestory.Client/GuiScreen*.cs "$lib"/Vintagestory.Client/ScreenManager.cs; do
  [[ -f "$f" ]] && perl -pi -e '
    s/(?<![.\w])Path\.(?=DirectorySeparatorChar|Combine|GetTempPath|GetFileName|GetExtension|GetDirectoryName|GetFullPath)/System.IO.Path./g;
  ' "$f"
done

# csvorbis.Block ambiguity in OggDecoder.
ogg="$lib/Vintagestory.Client.NoObf/OggDecoder.cs"
[[ -f "$ogg" ]] && perl -pi -e 's/\bBlock val/csvorbis.Block val/g; s/\bnew Block\(/new csvorbis.Block(/g' "$ogg"

# SystemRenderSunMoon: GL.GenQueries/GetQueryObject needs out not ref.
sun="$lib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs"
[[ -f "$sun" ]] && perl -pi -e '
  s/GL\.GenQueries\((\d+), ref (\w+)\)/GL.GenQueries($1, out $2)/g;
  s/GL\.GetQueryObject\(([^,]+), ([^,]+), ref (\w+)\)/GL.GetQueryObject($1, $2, out $3)/g;
' "$sun"

# Global RuntimeFieldHandle fixup: ILSpy cannot decompile inline array initializers (ldtoken for
# field handles that carry preinitialized byte blobs). Comment out all occurrences across the tree.
# Arrays remain zero-initialized; specific high-value arrays get correct values in fixups below.
find "$lib" -name '*.cs' -exec perl -pi -e 's{RuntimeHelpers\.InitializeArray\([^;]*RuntimeFieldHandle[^;]*\);}{// ILSpy: inline array init not supported}g' {} +



# SystemRenderOITLayers: RuntimeFieldHandle (ILSpy can not decompile inline array init).
oit="$lib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs"
[[ -f "$oit" ]] && perl -pi -e 's{RuntimeHelpers\.InitializeArray.*RuntimeFieldHandle.*LdMemberToken.*}{// ILSpy: inline array init not supported}' "$oit"

# DrawBuffersEnum inline array fixups: ILSpy emits zeroed arrays because it cannot decompile
# RuntimeFieldHandle-based array initializers. Inject the correct enum values from vanilla.
if [[ -f "$oit" ]]; then
  perl -0777 -pi -e 's/DrawBuffersEnum\[\] array = new DrawBuffersEnum\[6\];\s*\/\/ ILSpy: inline array init not supported\s*DrawBuffersEnum\[\] array2 = \(DrawBuffersEnum\[\]\)\(object\)array;/DrawBuffersEnum[] array2 = new DrawBuffersEnum[6] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3, DrawBuffersEnum.ColorAttachment4, DrawBuffersEnum.ColorAttachment5 };/' "$oit"
fi

# ClientPlatformWindows: remaining RuntimeFieldHandle occurrences + VSyncMode bool cast + ErrorCode ambiguity
# + BufferAccessMask|int cast + Keys→int cast + FramebufferErrorCode int cast + ref→out GL calls + Path ambiguity.
if [[ -f "$cpw" ]]; then
  perl -pi -e 's{RuntimeHelpers\.InitializeArray\(array[0-9]*, \(RuntimeFieldHandle\)/\*OpCode not supported: LdMemberToken\*/\);}{// ILSpy: inline array init not supported}g' "$cpw"
  # DrawBuffersEnum inline array fixups: replace zeroed arrays with correct initializers.
  # Pattern: new DrawBuffersEnum[N]; // ILSpy... arrayY = (cast)arrayX; → initialized array
  perl -0777 -pi -e '
    s/DrawBuffersEnum\[\] (\w+) = new DrawBuffersEnum\[4\];\s*\/\/ ILSpy: inline array init not supported\s*DrawBuffersEnum\[\] (\w+) = \(DrawBuffersEnum\[\]\)\(object\)\1;/DrawBuffersEnum[] $2 = new DrawBuffersEnum[4] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 };/g;
    s/DrawBuffersEnum\[\] (\w+) = new DrawBuffersEnum\[3\];\s*\/\/ ILSpy: inline array init not supported\s*DrawBuffersEnum\[\] (\w+) = \(DrawBuffersEnum\[\]\)\(object\)\1;/DrawBuffersEnum[] $2 = new DrawBuffersEnum[3] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2 };/g;
    s/DrawBuffersEnum\[\] (\w+) = new DrawBuffersEnum\[4\];\s*\/\/ ILSpy: inline array init not supported\s*(\w+) = \(DrawBuffersEnum\[\]\)\(object\)\1;/$2 = new DrawBuffersEnum[4] { DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2, DrawBuffersEnum.ColorAttachment3 };/g;
  ' "$cpw"
  perl -pi -e 's/\(VSyncMode\)enabled/enabled ? VSyncMode.On : VSyncMode.Off/g' "$cpw"
  perl -pi -e 's/(?<![.\w])ErrorCode(?= error| val)/OpenTK.Graphics.OpenGL.ErrorCode/g' "$cpw"
  perl -pi -e 's/error != ErrorCode\.NoError/error != OpenTK.Graphics.OpenGL.ErrorCode.NoError/g' "$cpw"
  # FramebufferErrorCode: cast int result and fix switch subtraction
  perl -pi -e 's/\*\(FramebufferErrorCode\*\)\(\&val\)/((FramebufferErrorCode)val)/g' "$cpw"
  perl -pi -e 's/switch \(val - 36053\)/switch ((int)val - 36053)/' "$cpw"
  # GL ref → out: for query/gen methods with nested casts in args (not ClearBuffer which uses ref as input)
  perl -pi -e 's/(GL\.(?:Get\w+|Gen\w+)\(.*?), ref (\w+)\)/$1, out $2)/g' "$cpw"
  # Path ambiguity in ClientPlatformWindows
  perl -pi -e 's/(?<![.\w])Path\.(?=DirectorySeparatorChar|Combine|GetTempPath|GetFileName|GetExtension|GetDirectoryName|GetFullPath)/System.IO.Path./g' "$cpw"
  # BufferAccessMask: val | int needs cast on the int literal or wrap in (int)val
  perl -pi -e 's/\(BufferAccessMask\)\(val \| (0x[0-9a-fA-F]+)\)/(BufferAccessMask)((int)val | $1)/g' "$cpw"
  # Keys enum to int: both the dictionary result AND the indexer need casts
  perl -pi -e 's/= KeyConverter\.NewKeysToGlKeys\[e\.Key\]/= (int)KeyConverter.NewKeysToGlKeys[(int)e.Key]/g' "$cpw"
fi

# 6g-extra: LoadedSoundNative.cs + AudioOpenAl.cs - OpenTK AL ref→out, EFX using alias, ALFormat int cast.
# Stratum fixes: AL.GetSource/GetBuffer use 'out' not 'ref'; EFX needs a using alias;
# soundFormat arithmetic needs (int) cast; Vector3 constructors use 'new' not _002Ector.
lsn="$repo_root/build/VintagestoryLib/Vintagestory.Client/LoadedSoundNative.cs"
if [[ -f "$lsn" ]]; then
  # Add EFX using alias after the OpenTK.Audio.OpenAL using (only if not present)
  if ! grep -q 'using EFX = ' "$lsn"; then
    perl -pi -e 's/^using OpenTK\.Audio\.OpenAL;$/$&\nusing EFX = OpenTK.Audio.OpenAL.ALC.EFX;/' "$lsn"
  fi
  # AL.GetSource/GetBuffer: ref → out
  perl -pi -e 's/AL\.GetSource\(([^,]+), ([^,]+), ref /AL.GetSource($1, $2, out /g' "$lsn"
  perl -pi -e 's/AL\.GetBuffer\(([^,]+), ([^,]+), ref /AL.GetBuffer($1, $2, out /g' "$lsn"
  # ALFormat - int arithmetic: soundFormat - 4354 needs (int) cast
  perl -pi -e 's/soundFormat - 4354/((int)soundFormat - 4354)/g' "$lsn"
  # Vector3 constructor: ((Vector3)(ref val))._002Ector(...) → val = new Vector3(...)
  perl -pi -e 's/\(\(Vector3\)\(ref (\w+)\)\)\._002Ector\(/$1 = new Vector3(/g' "$lsn"
fi

# AudioOpenAl.cs also uses EFX but decompiler drops the using alias.
aoa="$repo_root/build/VintagestoryLib/Vintagestory.Client/AudioOpenAl.cs"
if [[ -f "$aoa" ]]; then
  if ! grep -q 'using EFX = ' "$aoa"; then
    perl -pi -e 's/^using OpenTK\.Audio\.OpenAL;$/$&\nusing EFX = OpenTK.Audio.OpenAL.ALC.EFX;/' "$aoa"
  fi
fi

# 6g-extra: ClientProgram.cs - VSyncMode bool cast + ErrorCallback type alias.
# Stratum fix: add using alias for ErrorCallback, replace bool cast with ternary.
cprog="$repo_root/build/VintagestoryLib/Vintagestory.Client/ClientProgram.cs"
if [[ -f "$cprog" ]]; then
  # Add ErrorCallback using alias after OpenTK.Windowing.GraphicsLibraryFramework using
  if ! grep -q 'using ErrorCallback = ' "$cprog"; then
    perl -pi -e 's/^using OpenTK\.Windowing\.GraphicsLibraryFramework;$/$&\nusing ErrorCallback = OpenTK.Windowing.GraphicsLibraryFramework.GLFWCallbacks.ErrorCallback;/' "$cprog"
  fi
  # VSyncMode: (VSyncMode)(expr != 0) → expr != 0 ? VSyncMode.On : VSyncMode.Off
  perl -pi -e 's/\(VSyncMode\)\(ClientSettings\.VsyncMode != 0\)/ClientSettings.VsyncMode != 0 ? VSyncMode.On : VSyncMode.Off/g' "$cprog"
fi

# 6g-extra: ClientPlatformWindows.cs - Ext.CheckFramebufferStatus → GL.CheckFramebufferStatus,
# pointer cast simplification, FramebufferErrorCode subtraction needs (int) cast.
if [[ -f "$cpw" ]]; then
  # Ext.CheckFramebufferStatus → GL.CheckFramebufferStatus (Ext is not a real type)
  perl -pi -e 's/Ext\.CheckFramebufferStatus/GL.CheckFramebufferStatus/g' "$cpw"
  # FramebufferErrorCode pointer ToString: simplify the constrained prefix cast
  perl -pi -e 's/\(\(object\)\(\*\(FramebufferErrorCode\*\)\(\&val\)\)\/\*cast due to constrained\. prefix\*\/\)\.ToString\(\)/val.ToString()/g' "$cpw"
  # FramebufferErrorCode switch subtraction needs (int) cast
  perl -pi -e 's/switch \(val - 36053\)/switch ((int)val - 36053)/' "$cpw"
  # ErrorCode pointer cast in GlGetError: qualify the pointer type
  perl -pi -e 's/\*\(ErrorCode\*\)\(\&error\)\)/*(OpenTK.Graphics.OpenGL.ErrorCode*)(\&error))/g' "$cpw"
fi

# 6g-extra: base._002Ector(args)/this._002Ector(args) → : base(args)/: this(args)
# constructor initializers. ILSpy occasionally decompiles the base/this
# constructor chain call using its IL name instead of C#'s only legal syntax for
# it; this affects far more constructors than just GuiDialogCharacter (the one
# originally hand-fixed here), so scripts/fix-base-ctor-calls.py handles it
# project-wide. See that script's docstring for why moving the call is safe even
# when other code textually precedes it in the decompiled body.
python3 "$script_dir/fix-base-ctor-calls.py" "$repo_root/build/VintagestoryLib" "$repo_root/build/Vintagestory"

# 6g-extra: Vintagestory.csproj - needs ProjectReference to VintagestoryLib (not vanilla DLL).
# The per-OS entry class (ClientLinux/ClientWindows/ClientMac) uses the Vintagestory.Client namespace which lives in the VintagestoryLib project.
vs_entry_csproj="$repo_root/build/Vintagestory/Vintagestory.csproj"
if [[ -f "$vs_entry_csproj" ]]; then
  perl -pi -e 's|<Reference Include="VintagestoryLib">|<ProjectReference Include="..\\VintagestoryLib\\VintagestoryLib.csproj">|' "$vs_entry_csproj"
  perl -pi -e 's|<HintPath>[^<]*VintagestoryLib\.dll</HintPath>||' "$vs_entry_csproj"
  perl -pi -e 's|</Reference>|</ProjectReference>|' "$vs_entry_csproj"
fi

# 6h: Restore serialization metadata lost by ILSpy.
# ILSpy fails to decode attribute arguments for JsonObject(MemberSerialization.OptIn) and
# ProtoContract(ImplicitFields = ImplicitFields.AllFields). Without these, Newtonsoft serializes
# all public fields (breaking client/server config exchange) and protobuf-net serializes 0 bytes
# (breaking animation packet delivery in multiplayer).
echo "Restoring serialization metadata..."

# ProtoContract: animation/tag network packets need ImplicitFields.AllFields for protobuf-net
for pf in \
  "$lib/Vintagestory.Common.Network.Packets/AnimationPacket.cs" \
  "$lib/Vintagestory.Common.Network.Packets/BulkAnimationPacket.cs" \
  "$lib/Vintagestory.Common.Network.Packets/EntityTagPacket.cs" \
  "$lib/Vintagestory.Common.Network.Packets/MountAnimationPacket.cs"; do
  [[ -f "$pf" ]] && perl -pi -e 's/\[ProtoContract\]/[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]/' "$pf"
  [[ -f "$pf" ]] && perl -pi -e 's{\[ProtoContract\(/\*Could not decode attribute arguments\.\*/\)\]}{[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]}' "$pf"
done

# JsonObject: settings/config classes need MemberSerialization.OptIn
for jf in \
  "$lib/Vintagestory.Client.NoObf/ClientSettings.cs" \
  "$lib/Vintagestory.Client.NoObf/GltfPbrMetallicRoughness.cs" \
  "$lib/Vintagestory.Client.NoObf/GltfType.cs" \
  "$lib/Vintagestory.Client.NoObf/MacroBase.cs" \
  "$lib/Vintagestory.Common/SettingsBase.cs" \
  "$lib/Vintagestory.Common/StartServerArgs.cs" \
  "$lib/Vintagestory.Server/ServerConfig.cs" \
  "$lib/Vintagestory.Server/ServerPlayerData.cs" \
  "$lib/Vintagestory.Server/ServerSettings.cs"; do
  [[ -f "$jf" ]] && perl -pi -e 's/\[JsonObject\]/[JsonObject(MemberSerialization.OptIn)]/' "$jf"
  [[ -f "$jf" ]] && perl -pi -e 's{\[JsonObject\(/\*Could not decode attribute arguments\.\*/\)\]}{[JsonObject(MemberSerialization.OptIn)]}' "$jf"
done

# GltfAccessor: restore JsonProperty name + NullValueHandling args
gltf="$lib/Vintagestory.Client.NoObf/GltfAccessor.cs"
if [[ -f "$gltf" ]]; then
  perl -0777 -pi -e 's{\[JsonProperty\(/\*Could not decode attribute arguments\.\*/\)\]\n([ \t]*public double\[\] Max)}{[JsonProperty("max", NullValueHandling = NullValueHandling.Ignore)]\n$1}g' "$gltf"
  perl -0777 -pi -e 's{\[JsonProperty\(/\*Could not decode attribute arguments\.\*/\)\]\n([ \t]*public double\[\] Min)}{[JsonProperty("min", NullValueHandling = NullValueHandling.Ignore)]\n$1}g' "$gltf"
fi

echo "Serialization metadata restored."

# 6i-6m operate only on the decompiled projects (build/VintagestoryLib,
# build/Vintagestory), never on build/snapshot/ (the ilspycmd/fork clone cache) or
# real fork source under $repo_root/<Fork>. Some of these patterns (System.Func
# qualification, OrderedDictionary qualification) match genuine hand-written C# and
# would corrupt fork source like VintagestoryApi/Common/API/Delegates.cs if the find
# scope were widened to all of build/.
decompiled_dirs=("$repo_root/build/VintagestoryLib" "$repo_root/build/Vintagestory")

# 6i: .NET 9/10 added System.Collections.Generic.OrderedDictionary and the codebase
# separately defines Vintagestory.API.Common.Func (predates generic delegates) /
# Vintagestory.API.Datastructures.OrderedDictionary. Once both sides are in scope
# (via `using`), the bare names become ambiguous. Qualify with whichever side the
# actual VintagestoryApi interfaces expect: Vintagestory's own OrderedDictionary,
# but System.Func (IPlayerInventoryManager.Find and friends declare System.Func
# explicitly in VintagestoryApi/Common/Entity/Player/IPlayerInventoryManager.cs).
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/(?<!\.)\bOrderedDictionary</Vintagestory.API.Datastructures.OrderedDictionary</g;
  s/(?<!\.)\bFunc</System.Func</g;
'

# 6i-exceptions: a handful of interface members declare the *other* side explicitly,
# so the blanket qualification above picked the wrong one for them specifically.
# IWorldAccessor.FastSearchRecipesByIngredient declares System.Collections.Generic.
# OrderedDictionary (VintagestoryApi/Common/API/IWorldAccessor.cs); IClientEventAPI/
# IServerEventAPI.BeforeActiveSlotChanged declare Vintagestory.API.Common.Func
# (VintagestoryApi/Client/API/IClientEventAPI.cs, .../Server/API/IServerEventAPI.cs).
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/Vintagestory\.API\.Datastructures\.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>/System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>/g;
  s/System\.Func<ActiveSlotChangeEventArgs, EnumHandling>/Vintagestory.API.Common.Func<ActiveSlotChangeEventArgs, EnumHandling>/g;
  s/System\.Func<IServerPlayer, ActiveSlotChangeEventArgs, EnumHandling>/Vintagestory.API.Common.Func<IServerPlayer, ActiveSlotChangeEventArgs, EnumHandling>/g;
'

# ILSpy binds the cancellable EventHelper casts to System.Func, while vanilla IL uses Vintagestory.API.Common.Func.
# Run this exception after the Func qualification pass so full donor builds invoke handlers and patch extraction does not recreate an orphan patch.
evh="$lib/Vintagestory.API.Common/EventHelper.cs"
[[ -f "$evh" ]] && perl -pi -e '
  s/\(System\.Func<T, EnumHandling>\)handler/(Vintagestory.API.Common.Func<T, EnumHandling>)handler/;
  s/\(System\.Func<T0, T1, EnumHandling>\)handler/(Vintagestory.API.Common.Func<T0, T1, EnumHandling>)handler/;
' "$evh"

# 6j: ILSpy decompiles compiler-generated async state machines with plain
# `private void MoveNext()` / `private void SetStateMachine(...)` instead of explicit
# IAsyncStateMachine interface implementations, so the containing struct/class no
# longer satisfies IAsyncStateMachine. Convert to explicit interface implementation
# (signature is unique; IEnumerator.MoveNext() returns bool, not void, so this never
# collides with iterator MoveNext methods).
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/private void MoveNext\(\)/void IAsyncStateMachine.MoveNext()/g;
  s/private void SetStateMachine\(IAsyncStateMachine stateMachine\)/void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)/g;
'

# 6k: types with both an indexer and an explicit [DefaultMember("Item")] attribute
# conflict, because the compiler auto-emits DefaultMember for indexers. ILSpy adds
# the attribute back explicitly; strip it since the indexer already implies it.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -0pi -e '
  s/\[DefaultMember\("Item"\)\]\s*\n//g;
'

# 6l: ILSpy decompiles `fixed` buffer fields with both the `fixed` keyword AND an
# explicit [FixedBuffer] attribute; the compiler auto-emits the attribute for `fixed`
# fields, so the explicit one is a duplicate the compiler rejects (CS1716).
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -0pi -e '
  s/\[FixedBuffer\(.*?\)\]\s*\n(\s*public unsafe fixed)/$1/g;
'

# 6m: ILSpy decompiles destructors (~ClassName()) with an erroneous `virtual` prefix.
# Destructors cannot be marked virtual explicitly (the compiler already treats them
# as an override of Object.Finalize).
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/\bvirtual (~\w+\(\))/$1/g;
'

# 6n: NvidiaGPUFix64.cs (Windows Optimus GPU selection P/Invoke) has 12
# [UnmanagedFunctionPointer(/*Could not decode attribute arguments.*/)] delegates.
# All are extern P/Invoke callback delegates into nvapi64.dll, which uses the
# standard Windows API (__stdcall) calling convention.
nvfix="$repo_root/build/VintagestoryLib/Vintagestory/NvidiaGPUFix64.cs"
if [[ -f "$nvfix" ]]; then
  perl -pi -e 's/\[UnmanagedFunctionPointer\(\/\*Could not decode attribute arguments\.\*\/\)\]/[UnmanagedFunctionPointer(CallingConvention.StdCall)]/g' "$nvfix"
fi

# 6o: ConcurrentTagRegistry.cs backdoors private BCL fields via UnsafeAccessor but
# ILSpy could not decode the attribute arguments. Items() reads List<T>'s private
# _items array (same pattern as VintagestoryApi/Util/ListExtensions.cs); Object()
# reads ReadOnlyMemory<T>'s private _object reference.
ctr="$repo_root/build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistry.cs"
if [[ -f "$ctr" ]]; then
  perl -0pi -e 's/\[UnsafeAccessor\(\/\*Could not decode attribute arguments\.\*\/\)\]\s*\n(\s*public static extern ref T\[\] Items)/[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]\n$1/' "$ctr"
  perl -0pi -e 's/\[UnsafeAccessor\(\/\*Could not decode attribute arguments\.\*\/\)\]\s*\n(\s*public static extern ref object Object)/[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_object")]\n$1/' "$ctr"
fi

# 6o-2: bare `Enumerator<...>` locals ILSpy failed to qualify with their enclosing
# collection type (Dictionary<K,V>.Enumerator, List<T>.Enumerator,
# Dictionary<K,V>.ValueCollection.Enumerator, etc. all show up as the same bare
# shorthand). Rather than reconstruct which container each one belongs to, let the
# compiler infer it: every occurrence here is a local declared and immediately
# initialized from a `.GetEnumerator()` call, so `var` works for all of them
# uniformly regardless of which container is actually behind it. This must run
# before the per-file explicit-type fixes below (6p/6p-2): those exist only for
# the leftover bare Enumerator<...> declarations *without* an initializer, which
# `var` can't reach, but a same-named-but-different-collection bare Enumerator<...>
# can appear more than once in one file (e.g. ChatCommandApi.cs has both a
# Dictionary.Enumerator and a Dictionary.ValueCollection.Enumerator site sharing
# the identical bare shorthand) — running the per-file fix first would blanket
# every occurrence with one file-wide type, silently breaking the other one.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/\bEnumerator<[^;=]+>(\s+\w+\s*=\s*[^;]*\.GetEnumerator\(\));/var$1;/g;
'

# 6p: bare Enumerator<...> / ConfiguredTaskAwaiter / ConfiguredValueTaskAwaiter field
# types where ILSpy dropped the enclosing container type. Each is specific to the
# collection/task actually being enumerated/awaited at that call site.
cca="$repo_root/build/VintagestoryLib/Vintagestory.Common/ChatCommandApi.cs"
[[ -f "$cca" ]] && perl -pi -e 's/\bEnumerator<string, IChatCommand>/Dictionary<string, IChatCommand>.ValueCollection.Enumerator/g' "$cca"

for f in \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistry.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistryFast.cs"; do
  [[ -f "$f" ]] && perl -pi -e 's/\bEnumerator<string, ushort>/Dictionary<string, ushort>.Enumerator/g' "$f"
done

pim="$repo_root/build/VintagestoryLib/Vintagestory.Common/PlayerInventoryManager.cs"
[[ -f "$pim" ]] && perl -pi -e 's/\bEnumerator<IInventory>/List<IInventory>.Enumerator/g' "$pim"

se="$repo_root/build/VintagestoryLib/Vintagestory.Common/StreamExtensions.cs"
if [[ -f "$se" ]]; then
  perl -pi -e '
    s/\bConfiguredTaskAwaiter<(\w+)>/System.Runtime.CompilerServices.ConfiguredTaskAwaitable<$1>.ConfiguredTaskAwaiter/g;
    s/(?<!\.)\bConfiguredTaskAwaiter\b(?!\.)/System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter/g;
  ' "$se"
fi

mdu="$repo_root/build/VintagestoryLib/Vintagestory.ModDb/ModDbUtil.cs"
if [[ -f "$mdu" ]]; then
  perl -pi -e '
    s/\bConfiguredValueTaskAwaiter<(\w+)>/System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<$1>.ConfiguredValueTaskAwaiter/g;
    s/(?<!\.)\bConfiguredValueTaskAwaiter\b(?!\.)/System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter/g;
  ' "$mdu"
fi

# 6p-2: bare Enumerator<...> declared without an initializer (assigned later in
# separate branches, so `var` can't apply at the declaration site the way it does
# in 6q below). Each needs the container type read off its own `.GetEnumerator()`
# receiver.
gcs="$repo_root/build/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs"
[[ -f "$gcs" ]] && perl -pi -e 's/\bEnumerator<ConfigItem>/List<ConfigItem>.Enumerator/g' "$gcs"

sse="$repo_root/build/VintagestoryLib/Vintagestory.Client.NoObf/SystemSoundEngine.cs"
[[ -f "$sse" ]] && perl -pi -e 's/\bEnumerator<ILoadedSound>/Queue<ILoadedSound>.Enumerator/g' "$sse"

for f in \
  "$repo_root/build/VintagestoryLib/Vintagestory.Client/SystemClientCommands.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Server/CmdHelp.cs"; do
  [[ -f "$f" ]] && perl -pi -e 's/\bEnumerator<string, IChatCommand>/Dictionary<string, IChatCommand>.Enumerator/g' "$f"
done

ml="$repo_root/build/VintagestoryLib/Vintagestory.Common/ModLoader.cs"
if [[ -f "$ml" ]]; then
  perl -pi -e '
    s/\bEnumerator<ModContainer>/List<ModContainer>.Enumerator/g;
    s/\bEnumerator<ModSystem>/List<ModSystem>.Enumerator/g;
  ' "$ml"
fi


cl="$repo_root/build/VintagestoryLib/Vintagestory.Server/CmdLand.cs"
[[ -f "$cl" ]] && perl -pi -e 's/\bEnumerator<LandClaim>/List<LandClaim>.Enumerator/g' "$cl"

sbir="$repo_root/build/VintagestoryLib/Vintagestory.Server/ServerSystemBlockIdRemapper.cs"
[[ -f "$sbir" ]] && perl -pi -e 's/\bEnumerator<int, AssetLocation>/Dictionary<int, AssetLocation>.Enumerator/g' "$sbir"

# elementsByIndex is a Dictionary<long, long> storing T's raw bits (T : ILongIndex is
# assumed exactly long-sized), matching the read side's `*(long*)(&result)` pointer
# reinterpret a few lines above in the same file. ILSpy decompiled the write side as an
# illegal direct (long)elem cast instead of the matching reinterpret.
for f in \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common/ConcurrentIndexedFifoQueue.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common/IndexedFifoQueue.cs"; do
  [[ -f "$f" ]] && perl -pi -e 's/= \(long\)elem;/= Unsafe.As<T, long>(ref elem);/g' "$f"
done

# ILSpy decompiles a params ReadOnlySpan<T> element read as an unsafe pointer-cast
# Unsafe.Read<string>((void*)span[i]) instead of the plain indexer access it actually is.
for f in \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistry.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistryFast.cs"; do
  [[ -f "$f" ]] && perl -pi -e 's/System\.Runtime\.CompilerServices\.Unsafe\.Read<\w+>\(\(void\*\)([^;]+)\)/$1/g' "$f"
done

# ILSpy decompiles a reference-type null check as `(int)val != 0` (a value-type-style
# null pattern) instead of `val != null`; both files use the exact same generated shape.
for f in \
  "$repo_root/build/VintagestoryLib/Vintagestory.Server/BlockAccessorWorldGen.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Server/BlockAccessorWorldGenUpdateHeightmap.cs"; do
  [[ -f "$f" ]] && perl -pi -e 's/\(\(int\)val != 0\) \? \(\(object\)val\)\.ToString\(\) : null/val?.ToString()/g' "$f"
done

# 6r: ILSpy decompiles Try*(..., out x) BCL/API calls (TryGetValue, TryDequeue,
# TryPeek, TryPop, TryTake, TryRemove, TryParse, TryParseExact,
# TryGetNonEnumeratedCount, TryLoad) with `ref` instead of `out` at the call site.
# Every one of these overloads in this codebase (BCL Dictionary/ConcurrentDictionary/
# Enum/DateTime/NativeLibrary/Enumerable, and Vintagestory's own OrderedDictionary/
# FastSmallDictionary/ConcurrentSmallDictionary/RelaxedReadOnlyDictionary) declares
# the value parameter as `out`, never `ref`, so this is safe everywhere the pattern
# matches. The optional `<...>` group handles generic calls like
# Enum.TryParse<T>(...). The preceding-arguments group tolerates two levels of
# nested parens (covers casts chained with a member/method call, e.g.
# `((object)x).ToString()`). The final `ref` target is not restricted to a plain
# identifier: it can be a member access (`obj.Field`) or an unsafe pointer
# dereference (`*(long*)(&result)`), so that group accepts any run of
# non-comma/non-paren text interleaved with single-level parenthesized pieces.
# The call can also have ref as its only argument (no leading comma), which the
# optional `(?:,\s*)?` handles.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/\.(TryGetValue|TryDequeue|TryPeek|TryPop|TryTake|TryRemove|TryParse|TryParseExact|TryGetNonEnumeratedCount|TryLoad)((?:<[^<>]*>)?)\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*?)(?:,\s*)?ref\s+((?:[^,()]|\([^()]*\))+)\)/".$1$2(" . $3 . (length($3) ? ", " : "") . "out $4)"/ge;
'

# 6r-2: KeyValuePair<K,V>.Deconstruct(out K, out V) decompiles with both parameters
# as `ref`. Unlike the Try* fix above, Deconstruct can have more than one `ref`
# parameter to fix in the same call, so replace every `ref` inside the argument
# list rather than just the last one.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/\.Deconstruct\(((?:[^()]|\([^()]*\))*)\)/".Deconstruct(" . ($1 =~ s{\bref\b}{out}gr) . ")"/ge;
'

# 6r-3: one int.TryParse(..., ref num) call whose argument expression nests deeper
# than the one-level tolerance above (a cast, a Regex.Match, a Groups indexer, and
# a Replace call all inside the argument list). It is the only occurrence with
# this much nesting, so fix it directly by anchoring on the statement's end
# instead of writing a deeper generic paren-matcher.
shreg="$repo_root/build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs"
[[ -f "$shreg" ]] && perl -pi -e 's/(int\.TryParse\(.*), ref (\w+)\);/$1, out $2);/' "$shreg"

# 6r-4: ILSpy decompiles implicit conversion operators (decimal/Index/Memory<T>/
# ReadOnlyMemory<T>/ReadOnlySpan<T>/Span<T>/string's `implicit operator`) as an
# explicit call to their IL name (`op_Implicit`), which C# forbids calling
# directly (CS0571). An explicit cast invokes the same conversion operator and is
# always legal to write by hand. For every one of these except `string` the
# conversion targets the qualifying type itself (e.g. Span<byte>.op_Implicit(...)
# converts an ArraySegment<byte> into a Span<byte>), so casting to that same type
# name is correct. `string` is the one exception: its only op_Implicit converts a
# string into ReadOnlySpan<char>, not into another string, so it needs its own
# rule with a different target type.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/(?:System\.)?\bstring\.op_Implicit\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)/((System.ReadOnlySpan<char>)($1))/g;
  s/(?:System\.)?\b((?:Memory|ReadOnlyMemory|ReadOnlySpan|Span)<[^<>]*>|decimal|Index)\.op_Implicit\(((?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)/(($1)($2))/g;
'

# 6r-4b: `new System.ReadOnlySpan<char>(ref (char)expr)` — ReadOnlySpan<char>'s
# single-value constructor takes `in`, and a cast result is not an addressable
# lvalue, so `ref` can never bind here (CS1510). `in` parameters accept a
# temporary computed from any expression and do not require writing `in`
# explicitly at the call site, so dropping `ref` is enough.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/new System\.ReadOnlySpan<char>\(ref (\(char\)[^()]+(?:\([^()]*\))?[^()]*)\)/new System.ReadOnlySpan<char>($1)/g;
'

# 6r-5: bare Environment.SpecialFolder/SpecialFolderOption casts missing the
# Environment. qualifier (both nested enums, only reachable through it).
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/\((SpecialFolder|SpecialFolderOption)\)/(Environment.$1)/g;
'

# 6r-6: bare AppendInterpolatedStringHandler is really StringBuilder's nested
# interpolated-string-handler type (used by StringBuilder.Append($"...") /
# AppendLine(ref handler)), missing its StringBuilder. qualifier.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/(?<!\.)\bAppendInterpolatedStringHandler\b/StringBuilder.AppendInterpolatedStringHandler/g;
'

# 6s: custom-accessor events (public event T Name { add {...} remove {...} }) have
# no backing field of their own name, so ILSpy-decompiled reads of the event's
# current value (null checks, casts, GetInvocationList()) outside a += or -=
# context are CS0079. Every such event in this codebase follows the same
# Interlocked.CompareExchange pattern against a private m_<Name> field; rewrite
# reads to that field. Handled by a Python script (not a one-line regex) because
# it must skip the declaration line itself and never touch matches inside string
# literals (log/error messages that happen to mention the event name).
python3 "$script_dir/fix-event-reads.py" "${decompiled_dirs[@]}"

# 6t: nested async-lambda state machine structs (`private struct
# <DisplayClass>._003C<Method>Eb__N_M : IAsyncStateMachine`) are decompiled with
# their fields correctly public but the struct itself left private, even though
# the enclosing method instantiates and drives it from outside the display class
# that declares it. Making it public is always safe (never reduces access below
# what's needed) since these structs exist purely as compiler-generated plumbing.
find "${decompiled_dirs[@]}" -name '*.cs' -print0 | xargs -0 perl -pi -e '
  s/private struct (\w+) : IAsyncStateMachine/public struct $1 : IAsyncStateMachine/g;
'

# 6u: two hoisted-local fields (`_003C_003E8__1`, the state machine's own field
# for a variable that needs to survive an await) are read bare inside a nested
# `delegate { ... }` in async state machine structs. Reading a hoisted field bare
# requires an implicit `this.`, and C# forbids implicitly capturing `this` from a
# struct inside a nested anonymous method (CS1673). Each occurrence needs its own
# fix (a general "hoist every bare _003C_003E* access inside every lambda"
# pass was tried and reverted: it can't tell a field access apart from a
# _003C_003E-prefixed *type* reference used elsewhere, like
# `default(_003C_003Ec__DisplayClass9_0._003C_003Ce__b0_003Ed)`, and rewrote
# some of those into broken variable-used-as-a-type errors instead).
vswc="$repo_root/build/VintagestoryLib/Vintagestory.Common/VSWebClient.cs"
if [[ -f "$vswc" ]]; then
  perl -0777 -pi -e '
    s/(\t+)(Progress<int> val5 = new Progress<int>\(\(Action<int>\)delegate\(int totalBytes\)\n\1\{\n)\1\t_003C_003E8__1\.progress\.Report\(new Tuple<int, long>\(totalBytes, _003C_003E8__1\.contentLength\.Value\)\);/$1var downloadState = _003C_003E8__1;\n$1$2$1\tdownloadState.progress.Report(new Tuple<int, long>(totalBytes, downloadState.contentLength.Value));/;
  ' "$vswc"
fi

svc="$repo_root/build/VintagestoryLib/Vintagestory.Server/ServerConsole.cs"
if [[ -f "$svc" ]]; then
  python3 - "$svc" <<'PYEOF'
import re
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    text = f.read()

marker = "serverConsole.server.EnqueueMainThreadTask((Action)delegate"
if marker in text and "var consoleState = _003C_003E8__1;" not in text:
    idx = text.index(marker)
    line_start = text.rfind("\n", 0, idx) + 1
    indent = text[line_start:idx]

    brace_open = text.index("{", idx)
    depth = 1
    j = brace_open + 1
    while depth > 0:
        if text[j] == '{':
            depth += 1
        elif text[j] == '}':
            depth -= 1
        j += 1

    body = text[idx:j]
    new_body = re.sub(r'(?<![.\w])_003C_003E8__1\b', 'consoleState', body)
    new_text = (
        text[:line_start]
        + indent + "var consoleState = _003C_003E8__1;\n"
        + indent + new_body
        + text[j:]
    )
    with open(path, "w", encoding="utf-8") as f:
        f.write(new_text)
PYEOF
fi

# 6v: bare Convert.To*(...) calls in Vintagestory.Common.Database resolve to the
# sibling namespace Vintagestory.Common.Convert (ZstdNative.cs's namespace)
# instead of System.Convert, because a namespace that shares a prefix with the
# current one wins over a `using` import in C#'s lookup order. Scoped to just
# these two files: elsewhere in the codebase Convert.To* already means
# System.Convert without any ambiguity, so qualifying it everywhere would be an
# unnecessary, purely cosmetic change.
for f in \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common.Database/SQLiteDbConnectionv1.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common.Database/SQLiteDbConnectionv2.cs"; do
  [[ -f "$f" ]] && perl -pi -e 's/(?<!\.)\bConvert\.(To\w+)\(/System.Convert.$1(/g' "$f"
done

# 6w: `TYPE val = default(TYPE);` immediately followed by `val._002Ector(args);`
# — the value-type equivalent of the ref-cast constructor pattern 6e already
# handles, but without the `((Type)(ref var))` wrapper 6e's regex expects, so it
# needs its own pass. Replaces both statements with a single `TYPE val = new
# TYPE(args);`, matching parens/braces properly so multi-line constructor
# arguments (object initializers, nested delegates) come through intact.
python3 - "${decompiled_dirs[@]}" <<'PYEOF'
import re
import sys
import glob

files = []
for root in sys.argv[1:]:
    files.extend(glob.glob(root + "/**/*.cs", recursive=True))

decl_re = re.compile(r'([ \t]*)([\w<>,.\s]+?)\s+(\w+)\s*=\s*default\(\2\);\n')


def find_matching_paren(s, open_idx):
    depth = 1
    j = open_idx + 1
    while depth > 0:
        if s[j] == '(':
            depth += 1
        elif s[j] == ')':
            depth -= 1
        j += 1
    return j


total = 0
for path in files:
    with open(path, encoding="utf-8") as f:
        text = f.read()
    if '_002Ector(' not in text:
        continue

    out = []
    pos = 0
    changed = False
    for m in decl_re.finditer(text):
        if m.start() < pos:
            continue
        indent, typename, varname = m.group(1), m.group(2), m.group(3)
        call_marker = f'{varname}._002Ector('
        after = text[m.end():]
        stripped = after.lstrip('\t ')
        if not stripped.startswith(call_marker):
            continue
        call_start = m.end() + (len(after) - len(stripped))
        paren_open = text.index('(', call_start)
        paren_close = find_matching_paren(text, paren_open)
        if text[paren_close] != ';':
            continue
        args = text[paren_open + 1:paren_close - 1]
        out.append(text[pos:m.start()])
        out.append(f'{indent}{typename} {varname} = new {typename}({args});\n')
        pos = paren_close + 2
        changed = True
        total += 1

    if changed:
        out.append(text[pos:])
        with open(path, 'w', encoding='utf-8') as f:
            f.write(''.join(out))

print(f"Rewrote {total} value-type constructor call(s).")
PYEOF

# 6x: remaining Phase 4 long-tail singles/small-groups, each verified to be the
# only occurrence of its pattern (checked via grep across build/ before writing
# the fix) unless noted otherwise.

# Mutex's 3-arg constructor declares the third parameter `out bool createdNew`;
# ILSpy rendered the call site with `ref`.
cp_mutex="$repo_root/build/VintagestoryLib/Vintagestory.Client/ClientProgram.cs"
[[ -f "$cp_mutex" ]] && perl -pi -e 's/new Mutex\(true, "Vintagestory", ref flag\);/new Mutex(true, "Vintagestory", out flag);/' "$cp_mutex"

# FieldAttributes (a [Flags] enum) has no operator& with a bare int literal;
# the original source must have cast to int first, and ILSpy dropped the cast.
snp="$repo_root/build/VintagestoryLib/Vintagestory.Client.NoObf/SystemNetworkProcess.cs"
[[ -f "$snp" ]] && perl -pi -e 's/\(fields\[i\]\.Attributes & 0x40\)/((int)fields[i].Attributes & 0x40)/' "$snp"

# A destructor's compiler-generated try/finally already chains to the base
# finalizer implicitly; C# forbids calling Finalize() explicitly at all (it's
# protected on object, reachable only via that implicit chain), so ILSpy's
# rendering of that implicit chain as an explicit call must simply be dropped.
vao="$repo_root/build/VintagestoryLib/Vintagestory.Client.NoObf/VAO.cs"
[[ -f "$vao" ]] && perl -ni -e 'print unless /^\s*\(\(object\)this\)\.Finalize\(\);\s*$/' "$vao"

# Socket.Dispose(bool) is protected; casting `this` to the base type Socket and
# calling through that cast strips the derived-class access that protected
# members need (CS1540), whereas `base.Dispose(disposing)` is exactly what an
# override calling its base implementation should be.
vss="$repo_root/build/VintagestoryLib/Vintagestory.Client.NoObf/VintageStorySocket.cs"
[[ -f "$vss" ]] && perl -pi -e 's/\(\(Socket\)this\)\.Dispose\(disposing\);/base.Dispose(disposing);/' "$vss"

# ConcurrentIndexedFifoQueue<T> reinterprets its ConcurrentDictionary<long, T>
# field as ConcurrentDictionary<long, long> everywhere (T : ILongIndex is
# always exactly 8 bytes here; every other read/write in this file already
# goes through the same (Type)(object) round-trip). Snapshot() reads through
# that reinterpret but was missing the matching cast back to ICollection<T> on
# the way out.
cifq="$repo_root/build/VintagestoryLib/Vintagestory.Common/ConcurrentIndexedFifoQueue.cs"
[[ -f "$cifq" ]] && perl -pi -e 's/return \(\(ConcurrentDictionary<long, long>\)\(object\)elementsByIndex\)\.Values;/return (System.Collections.Generic.ICollection<T>)(object)((ConcurrentDictionary<long, long>)(object)elementsByIndex).Values;/' "$cifq"

# TagSet is a readonly struct, so its `storage` field can only be taken by `ref`
# inside TagSet's own constructor; `in` is what Unsafe.AsRef<T>(in T) actually
# accepts, and it is indistinguishable from `ref` at the IL level, so ILSpy
# picked the wrong keyword.
ctr="$repo_root/build/VintagestoryLib/Vintagestory.Common.Datastructures/ConcurrentTagRegistry.cs"
[[ -f "$ctr" ]] && perl -pi -e 's/Unsafe\.AsRef<ReadOnlyMemory<ushort>>\(ref set\.storage\)/Unsafe.AsRef<ReadOnlyMemory<ushort>>(in set.storage)/' "$ctr"


# VSWebClient's async state machine funnels any exception caught while
# disposing an await-using resource through an `object`-typed hoisted field so
# it can be rethrown later via ExceptionDispatchInfo. C# requires catch/throw
# clauses to be statically typed as Exception (or a subtype); the field stays
# `object` (matching real Roslyn codegen for this pattern), but the catch
# clause and the later rethrow need to talk about it as Exception explicitly.
if [[ -f "$vswc" ]]; then
  perl -pi -e '
    s/catch \(object obj\)/catch (System.Exception obj)/;
    s/ExceptionDispatchInfo\.Capture\(\(obj2 as System\.Exception\) \?\? throw obj2\)\.Throw\(\);/ExceptionDispatchInfo.Capture((System.Exception)obj2).Throw();/;
  ' "$vswc"
fi

# `fixed (T* p = someSpan)` is special-cased by the compiler, but explicitly
# calling `someSpan.GetPinnableReference()` yourself and using that call's
# result as the fixed initializer only satisfies the general pattern-based
# fixed rules when the byref-returning call is prefixed with `&`; ILSpy always
# drops that `&` since IL has no separate "address-of" opcode to decompile it
# from. Verified against all 3 occurrences in the codebase (all identical:
# ZSTD (de)compressor wrappers pinning a ReadOnlySpan<byte>/Span<byte>).
for f in \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common/ZStdCompressorImpl.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common/ZStdDecompressorImpl.cs" \
  "$repo_root/build/VintagestoryLib/Vintagestory.Common/ZStdWrapper.cs"; do
  [[ -f "$f" ]] && perl -pi -e 's/fixed \(byte\* (\w+) = (\w+(?:\.\w+)?)\.GetPinnableReference\(\)\)/fixed (byte* $1 = &$2.GetPinnableReference())/' "$f"
done

# PosixSignal defines `enum operator-(PosixSignal, int)` returning PosixSignal,
# so `signal - -4` is itself a PosixSignal even though only its underlying
# numeric value is meant to drive the switch; only the literal 0 has an
# implicit conversion to an enum type, so the nonzero case labels (2, 1) fail
# to compile. Casting to int first recovers the same numeric value the
# original switch clearly intended.
sp="$repo_root/build/VintagestoryLib/Vintagestory.Server/ServerProgram.cs"
[[ -f "$sp" ]] && perl -pi -e 's/\(signal - -4\) switch/((int)signal - -4) switch/' "$sp"

echo "Ambiguity and decompiler-artifact fixups applied."

# Snapshot: save post-fixup state as .baseline/ for patch extraction.
# extract-patches.sh diffs the working tree against .baseline/ to produce patches.
echo "Saving post-fixup baseline snapshot..."
baseline_dir="$repo_root/.baseline"
rm -rf "$baseline_dir"
mkdir -p "$baseline_dir"
# Decompiled + fixupped projects.
cp -r "$repo_root/build/VintagestoryLib" "$baseline_dir/VintagestoryLib"
cp -r "$repo_root/build/Vintagestory" "$baseline_dir/Vintagestory"
# Fork projects (from snapshot, already LF-normalized in step 3).
for fork_snap in "$snapshot_dir"/*/; do
  name="$(basename "$fork_snap")"
  # Skip decompiled projects (already handled above).
  [[ "$name" == "VintagestoryLib" || "$name" == "Vintagestory" ]] && continue
  cp -r "$fork_snap" "$baseline_dir/$name"
done
# Apply sources/ overlays to the baseline too, so that extract-patches.sh
# does not generate spurious diffs for files managed by the overlay (e.g.
# VSEssentialsMod.csproj). Only .csproj/.props/.targets are overlaid here;
# .cs files in sources/ are Optimum-original and belong in sources/, not baseline.
if [[ -d "$sources_dir" ]]; then
  while IFS= read -r -d '' src; do
    rel="${src#$sources_dir/}"
    top_proj="$(echo "$rel" | cut -d/ -f1)"
    target="$baseline_dir/$top_proj/$(echo "$rel" | cut -d/ -f2-)"
    if [[ -f "$target" ]]; then
      cp -f "$src" "$target"
    fi
  done < <(find "$sources_dir" -type f \( -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) -print0)
fi
echo "Baseline snapshot saved to .baseline/."

# 7. Apply optimization patches on top of the post-fixup baseline.
#
# Patch directory: patches/{project}/file.patch
# Projects VintagestoryLib and Vintagestory target build/ (--directory=build).
# All other projects (VintagestoryApi, VSEssentials, etc.) target repo root.
#
# Environment variables:
#   PATCH_FILTER  - "all" (default), or comma-separated substrings to EXCLUDE
#                   Example: PATCH_FILTER="AnimationUtil,SystemRender" skips those patches

patches_dir="$repo_root/patches"
patch_filter="${PATCH_FILTER:-all}"
vanilla_patch_projects="VintagestoryLib Vintagestory"

if [[ -d "$patches_dir" ]] && find "$patches_dir" -name '*.patch' -print -quit | grep -q .; then

  # ZIP downloads (non-clone) lack a .git/ directory. git add and git apply
  # both exit 128 without one. Create a temporary repo so patches can apply.
  # A marker file tags the temporary repo: when an interrupted run leaves it
  # behind, its stale index and lock files make the next git add die with a
  # silent exit 128 under set -e, so throw it away and start clean.
  _optimum_tmp_git=false
  if [[ -f "$repo_root/.git/optimum-bootstrap-tmp" ]]; then
    rm -rf "$repo_root/.git"
  fi
  if [[ ! -d "$repo_root/.git" ]]; then
    git init -q "$repo_root"
    touch "$repo_root/.git/optimum-bootstrap-tmp"
    _optimum_tmp_git=true
  fi

  # Stage into git index for cleaner apply diagnostics. Staging failures are
  # not fatal: git apply reports per-patch failures below on its own.
  git add -f build/ VintagestoryApi/ Cairo/ VSEssentials/ VSSurvivalMod/ VSCreativeMod/ 2>/dev/null || true

  failed=()
  applied=0
  skipped=0

  while IFS= read -r -d '' patch; do
    rel="${patch#$repo_root/}"

    # Filter check: skip patches matching any excluded substring.
    if [[ "$patch_filter" != "all" ]]; then
      skip=false
      IFS=',' read -ra excludes <<< "$patch_filter"
      for excl in "${excludes[@]}"; do
        if [[ "$rel" == *"$excl"* ]]; then
          skip=true
          break
        fi
      done
      if $skip; then
        ((skipped++)) || true
        continue
      fi
    fi

    echo "Applying $rel"
    top_proj="$(echo "$rel" | cut -d/ -f2)"
    dir_arg=""
    if echo "$vanilla_patch_projects" | grep -qw "$top_proj"; then
      dir_arg="--directory=build"
    fi

    if ! output="$(git_apply_optimum_patch "$patch" "$dir_arg" 2>&1)"; then
      failed+=("$rel")
      echo "  FAILED: $output" | head -3
    else
      if [[ -n "$output" ]]; then
        echo "  $output"
      fi
      ((applied++)) || true
    fi
  done < <(find "$patches_dir" -type f -name '*.patch' -print0 | sort -z)

  # Unstage: the index staging was temporary.
  git reset HEAD -- build/ VintagestoryApi/ Cairo/ VSEssentials/ VSSurvivalMod/ VSCreativeMod/ >/dev/null 2>&1 || true

  # Remove the temporary .git/ created for ZIP-download users.
  if [[ "$_optimum_tmp_git" == true ]]; then
    rm -rf "$repo_root/.git"
  fi

  echo "Patches: $applied applied, $skipped skipped, ${#failed[@]} failed (filter: $patch_filter)"
  if [[ "${#failed[@]}" -gt 0 ]]; then
    printf '  %s\n' "${failed[@]}" >&2
    exit 1
  fi
else
  echo "No patches/ to apply."
fi

# 8. Copy Optimum-only source files.
if [[ -d "$sources_dir" ]]; then
  while IFS= read -r -d '' src; do
    rel="${src#$sources_dir/}"
    # For vanilla (decompiled) projects, the working tree is under build/.
    top_proj="$(echo "$rel" | cut -d/ -f1)"
    # lang/ and shaders/ are deploy-time asset overlays, not project source.
    # deploy and the package scripts read them from sources/ directly; copying
    # them here dumped stray lang/ and shaders/ dirs at the repo root.
    case "$top_proj" in
      lang|shaders) continue ;;
    esac
    if echo "$vanilla_patch_projects" | grep -qw "$top_proj"; then
      target="$repo_root/build/$rel"
    else
      target="$repo_root/$rel"
    fi
    mkdir -p "$(dirname "$target")"
    cp -f "$src" "$target"
  done < <(find "$sources_dir" -type f -print0)
  echo "Synced sources/ into working tree."

# CS0246: _003C_003Ec closure class references (must run after patches restore the files).
perl "$repo_root/scripts/fix-closure-class.pl" \
  "$lib/Vintagestory.Client/ScreenManager.cs" \
  "$lib/Vintagestory.Client/GuiScreenRunningGame.cs"

fi

# Completion stamp: make build reruns bootstrap while this file is missing, so
# a run that dies halfway cannot wedge the tree in a half-bootstrapped state
# where build/ exists but the forks never got their patches.
touch "$repo_root/.bootstrap-complete"

echo "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release"
