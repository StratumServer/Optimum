#!/usr/bin/env bash
set -eu
if (set -o pipefail 2>/dev/null); then
    set -o pipefail
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
vanilla_dir="${VANILLA_DIR:-$repo_root/.vanilla/win-x64/vintagestory}"
vanilla_parent="$(cd -- "$vanilla_dir/.." && pwd)"
runtime_donor_dir="${RUNTIME_DONOR_DIR:-$vanilla_parent/runtime-donors}"
configuration="${CONFIGURATION:-Release}"
runtime_root="$repo_root/.build/runtime-donors"
contracts_dll="$repo_root/bin/$configuration/net10.0/Optimum.Api.Contracts.dll"
game_content_dll="$repo_root/bin/$configuration/net10.0/Optimum.GameContent.dll"
api_dll="$repo_root/bin/$configuration/net10.0/VintagestoryAPI.dll"

hash_files() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$@"
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$@"
    else
        echo "sha256sum or shasum is required for runtime donor validation." >&2
        exit 1
    fi
}

check_manifest() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum --check --status runtime-donor-manifest.sha256
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 --check runtime-donor-manifest.sha256 >/dev/null
    else
        echo "sha256sum or shasum is required for runtime donor validation." >&2
        return 1
    fi
}

normalize_lf() {
    find "$1" -type f -name '*.patch' -print0 |
        while IFS= read -r -d '' file; do perl -0pi -e 's/\r\n/\n/g; s/\r/\n/g' "$file"; done
}

# Escapes a filesystem path for safe use as XML element text (e.g. inside
# <HintPath>). Install paths are user-controlled and may contain characters
# like '&' that are otherwise invalid XML and break MSBuild parsing.
xml_escape() {
    printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'
}

# Converts a path to the platform-native form MSBuild can resolve.
# On Git Bash / MINGW (cygpath available), converts /d/a/... to D:\a\...
# On Linux/macOS, returns the input unchanged.
native_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

normalize_lf "$repo_root/patches/runtime"

dotnet_tools_dir="${DOTNET_TOOLS_DIR:-$HOME/.dotnet/tools}"
if [[ -x "$dotnet_tools_dir/ilspycmd" ]]; then
    export PATH="$dotnet_tools_dir:$PATH"
fi
if ! command -v ilspycmd >/dev/null 2>&1; then
    echo "ilspycmd is required. Run scripts/bootstrap.sh first." >&2
    exit 1
fi

for required in \
    "$runtime_donor_dir/VintagestoryAPI.dll" \
    "$runtime_donor_dir/Mods/VSEssentials.dll" \
    "$runtime_donor_dir/Mods/VSEssentials.pdb" \
    "$runtime_donor_dir/Mods/VSCreativeMod.dll" \
    "$runtime_donor_dir/Mods/VSSurvivalMod.dll" \
    "$runtime_donor_dir/Mods/VSSurvivalMod.pdb" \
    "$runtime_donor_dir/runtime-donor-version.txt" \
    "$runtime_donor_dir/runtime-donor-manifest.sha256" \
    "$api_dll" \
    "$contracts_dll" \
    "$game_content_dll"; do
    if [[ ! -f "$required" ]]; then
        echo "Required runtime-donor input not found: $required" >&2
        exit 1
    fi
done

if ! (
    cd "$runtime_donor_dir"
    manifest_paths="$(sed -E 's/^[0-9a-fA-F]{64}[[:space:]]{2}//' runtime-donor-manifest.sha256 | sort)"
    actual_paths="$(find . -type f -not -name 'runtime-donor-manifest.sha256' -print | sort)"
    [[ "$manifest_paths" == "$actual_paths" ]]
    check_manifest
    live_version_file="$(find "$vanilla_dir/assets" -maxdepth 1 -name 'version-*.txt' -print -quit 2>/dev/null || true)"
    live_version="${live_version_file##*/}"
    snapshot_version="$(tr -d '\r\n' < runtime-donor-version.txt)"
    [[ -n "$live_version" && "$live_version" == "$snapshot_version" ]]
); then
    echo "Runtime donor snapshot failed integrity validation: $runtime_donor_dir" >&2
    echo "Run scripts/bootstrap.sh --refresh to recreate the vanilla snapshot." >&2
    exit 1
fi
if cmp -s "$api_dll" "$runtime_donor_dir/VintagestoryAPI.dll"; then
    echo "Compiled VintagestoryAPI.dll matches the vanilla snapshot; rebuild the patched API before donor validation." >&2
    exit 1
fi

# This directory contains generated source only. It is reconstructed from the
# protected vanilla runtime-donor snapshot.
rm -rf -- "$runtime_root"
mkdir -p "$runtime_root"

decompile_mod() {
    local project="$1"
    local assembly="$2"
    local output="$runtime_root/$project"

    echo "Decompiling exact $project runtime donor..."
    local -a reference_args=()
    local reference_dir
    for reference_dir in "$repo_root/bin/$configuration/net10.0" "$runtime_donor_dir" "$runtime_donor_dir/Lib" "$runtime_donor_dir/Mods"; do
        if [[ -d "$reference_dir" ]]; then
            reference_args+=(--referencepath "$reference_dir")
        else
            echo "WARNING: Vanilla reference directory not found: $reference_dir" >&2
        fi
    done

    local project_file
    local attempt
    for attempt in 1 2 3; do
        rm -rf -- "$output"
        ilspycmd \
            --project \
            --nested-directories \
            "${reference_args[@]}" \
            --outputdir "$output" \
            "$assembly" >/dev/null
        project_file="$(find "$output" -maxdepth 1 -name '*.csproj' -size +0c -print -quit)"
        if [[ -n "$project_file" ]]; then
            break
        fi
        echo "ilspycmd produced an incomplete $project project, retrying ($attempt/3)..." >&2
    done
    if [[ -z "${project_file:-}" ]]; then
        echo "ilspycmd did not create a usable project for $project." >&2
        exit 1
    fi

    # ILSpy writes a repository-relative placeholder path and currently emits
    # the next language-version number. Runtime donors are ignored build
    # artifacts, so resolve references to the selected owned client and use
    # the compiler's supported preview mode.
    local vanilla_win_path="$vanilla_dir"
    if command -v cygpath >/dev/null 2>&1; then
        vanilla_win_path="$(cygpath -w "$vanilla_dir")"
    fi
    local native_donor_root
    native_donor_root="$(native_path "$runtime_donor_dir/")"
    RUNTIME_DONOR_HINT_ROOT="$(xml_escape "$native_donor_root")" \
    VANILLA_DIR_ESC="$(xml_escape "$vanilla_dir/")" \
    VANILLA_WIN_PATH="$(xml_escape "$vanilla_win_path")" perl -0pi -e '
        my $root = $ENV{RUNTIME_DONOR_HINT_ROOT};
        # Relative path produced by ilspycmd on Linux/macOS
        s#<HintPath>\.vanilla/win-x64/vintagestory/#<HintPath>${root}#g;
        # Unix-style absolute path (Git Bash MINGW)
        (my $vanilla_fwd = $ENV{VANILLA_DIR_ESC}) =~ s/\\/\//g;
        s#<HintPath>\Q${vanilla_fwd}\E/#<HintPath>${root}#gi;
        s#<HintPath>\Q${vanilla_fwd}\E\\#<HintPath>${root}#gi;
        # Windows-style absolute path from cygpath -w (D:\a\...\vintagestory)
        my $win = $ENV{VANILLA_WIN_PATH};
        if ($win) {
            (my $win_fwd = $win) =~ s/\\/\//g;
            (my $win_bwd = $win) =~ s/\//\\/g;
            s#<HintPath>\Q${win_fwd}\E[/\\]#<HintPath>${root}#gi;
            s#<HintPath>\Q${win_bwd}\E[/\\]#<HintPath>${root}#gi;
        }
        s#<LangVersion>\d+\.\d+</LangVersion>#<LangVersion>preview</LangVersion>#g;
        s#</PropertyGroup>#  <Nullable>disable</Nullable>\n    <NoWarn>\$(NoWarn);0618;8632;0420;0649;0169;9193;9113</NoWarn>\n  </PropertyGroup>#;
        s#</PropertyGroup>#</PropertyGroup>\n  <ItemGroup>\n    <FrameworkReference Include="Microsoft.NETCore.App" />\n  </ItemGroup>#
            unless /<FrameworkReference\s+Include="Microsoft\.NETCore\.App"/;
    ' "$project_file"
}

decompile_mod \
    "VSEssentials" \
    "$runtime_donor_dir/Mods/VSEssentials.dll"
decompile_mod \
    "VSSurvivalMod" \
    "$runtime_donor_dir/Mods/VSSurvivalMod.dll"

add_reference() {
    local project_file="$1"
    local include="$2"
    local hint_path="$3"
    PROJECT_INCLUDE="$include" PROJECT_HINT_PATH="$(xml_escape "$hint_path")" perl -0pi -e '
        s#<ItemGroup>#<ItemGroup>\n    <Reference Include="$ENV{PROJECT_INCLUDE}">\n      <HintPath>$ENV{PROJECT_HINT_PATH}</HintPath>\n      <Private>false</Private>\n    </Reference>#;
    ' "$project_file"
}

set_reference_hint_path() {
    local project_file="$1"
    local include="$2"
    local hint_path="$3"
    PROJECT_INCLUDE="$include" PROJECT_HINT_PATH="$(xml_escape "$hint_path")" perl -0pi -e '
        s#<Reference Include="$ENV{PROJECT_INCLUDE}"(?:\s*/>|>.*?</Reference>)#<Reference Include="$ENV{PROJECT_INCLUDE}">\n      <HintPath>$ENV{PROJECT_HINT_PATH}</HintPath>\n      <Private>false</Private>\n    </Reference>#s;
    ' "$project_file"
}

exclude_compile_items() {
    local project_file="$1"
    shift
    local item
    local removes=$'<ItemGroup Label="Runtime donor excludes">\n'
    for item in "$@"; do
        removes+="    <Compile Remove=\"$item\" />"$'\n'
    done
    removes+=$'</ItemGroup>\n'
    RUNTIME_COMPILE_REMOVES="$removes" perl -0pi -e '
        s#</Project>#$ENV{RUNTIME_COMPILE_REMOVES}</Project># if index($_, "Runtime donor excludes") < 0;
    ' "$project_file"
}

essentials_project="$(find "$runtime_root/VSEssentials" -maxdepth 1 -name '*.csproj' -print -quit)"
survival_project="$(find "$runtime_root/VSSurvivalMod" -maxdepth 1 -name '*.csproj' -print -quit)"

# On Windows (Git Bash), HintPaths must use native Windows paths for MSBuild.
native_contracts_dll="$(native_path "$contracts_dll")"
native_game_content_dll="$(native_path "$game_content_dll")"
native_api_dll="$(native_path "$api_dll")"
native_runtime_donor_dir="$(native_path "$runtime_donor_dir")"

add_reference "$essentials_project" "Optimum.Api.Contracts" "$native_contracts_dll"
add_reference "$essentials_project" "Optimum.GameContent" "$native_game_content_dll"
add_reference "$survival_project" "Optimum.Api.Contracts" "$native_contracts_dll"
set_reference_hint_path "$essentials_project" "VintagestoryAPI" "$native_api_dll"
set_reference_hint_path "$survival_project" "VintagestoryAPI" "$native_api_dll"
set_reference_hint_path \
    "$survival_project" \
    "VSCreativeMod" \
    "$(native_path "$runtime_donor_dir/Mods/VSCreativeMod.dll")"
exclude_compile_items "$survival_project" \
    "Vintagestory/GameContent/ModSystemVillagerDebug.cs" \
    "Vintagestory/ServerMods/ChiselBlockBulkSetMaterial.cs" \
    "Vintagestory/ServerMods/UpgradeTasks.cs"

resolve_references() {
    local project_file="$1"
    local native_donor_dir="$(native_path "$runtime_donor_dir")"
    RUNTIME_DONOR_DIR="$runtime_donor_dir" NATIVE_DONOR_DIR="$native_donor_dir" perl -0pi -e '
        my @dirs = ("$ENV{RUNTIME_DONOR_DIR}/Lib", $ENV{RUNTIME_DONOR_DIR}, "$ENV{RUNTIME_DONOR_DIR}/Mods");
        my @native_dirs = ("$ENV{NATIVE_DONOR_DIR}/Lib", $ENV{NATIVE_DONOR_DIR}, "$ENV{NATIVE_DONOR_DIR}/Mods");
        # On Windows the native dir uses backslash; normalise to forward for joining
        for my $nd (@native_dirs) { $nd =~ s#\\#/#g; }
        sub xml_escape {
            my $s = shift;
            $s =~ s/&/&amp;/g;
            $s =~ s/</&lt;/g;
            $s =~ s/>/&gt;/g;
            return $s;
        }
        sub native_hint {
            my ($unix_path) = @_;
            # Replace each probe-dir prefix with the corresponding native dir
            for my $i (0..$#dirs) {
                my $d = $dirs[$i];
                if (index($unix_path, $d) == 0) {
                    my $rest = substr($unix_path, length($d));
                    return $native_dirs[$i] . $rest;
                }
            }
            return $unix_path;
        }
        # Pass 1: Self-closing references without HintPath
        s{<Reference Include="([^"]+)"\s*/>}{
            my $name = $1;
            my $found = "";
            for my $dir (@dirs) {
                my $probe = "$dir/$name.dll";
                if (-f $probe) {
                    $found = $probe;
                    last;
                }
            }
            if ($found) {
                my $hint = native_hint($found);
                qq{<Reference Include="$name">\n      <HintPath>} . xml_escape($hint) . qq{</HintPath>\n      <Private>false</Private>\n    </Reference>};
            } else {
                qq{<Reference Include="$name" />};
            }
        }ge;
        # Pass 2: References with HintPath that does not exist on disk
        s{<Reference Include="([^"]+)">\s*<HintPath>([^<]+)</HintPath>\s*</Reference>}{
            my $name = $1;
            my $hint = $2;
            if (-f $hint) {
                qq{<Reference Include="$name">\n      <HintPath>$hint</HintPath>\n    </Reference>};
            } else {
                my $found = "";
                for my $dir (@dirs) {
                    my $probe = "$dir/$name.dll";
                    if (-f $probe) {
                        $found = $probe;
                        last;
                    }
                }
                if ($found) {
                    my $hint = native_hint($found);
                    qq{<Reference Include="$name">\n      <HintPath>} . xml_escape($hint) . qq{</HintPath>\n      <Private>false</Private>\n    </Reference>};
                } else {
                    qq{<Reference Include="$name">\n      <HintPath>$hint</HintPath>\n    </Reference>};
                }
            }
        }ge;
    ' "$project_file"
}
resolve_references "$essentials_project"
resolve_references "$survival_project"

"$repo_root/scripts/runtime-donor-patch-gate.sh" "$repo_root" ".build/runtime-donors"
eligible_projects=(VSEssentials VSSurvivalMod)

# Optimum-owned new types are source overlays, not decompiled game source.
if [[ " ${eligible_projects[*]} " == *" VSEssentials "* ]]; then
    cp -f \
        "$repo_root/sources/VSEssentials/Systems/OptimumStatus.cs" \
        "$runtime_root/VSEssentials/Vintagestory/GameContent/OptimumStatusModSystem.cs"
fi
if [[ " ${eligible_projects[*]} " == *" VSSurvivalMod "* ]]; then
    cp -f \
        "$repo_root/sources/VSSurvivalMod/BlockEntityRenderer/CrucibleInFirepitRenderer.cs" \
        "$runtime_root/VSSurvivalMod/Vintagestory/GameContent/CrucibleInFirepitRenderer.cs"
    cp -f \
        "$repo_root/sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs" \
        "$runtime_root/VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitShapeCache.cs"
    cp -f \
        "$repo_root/sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs" \
        "$runtime_root/VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitAnimatorCache.cs"
    cp -f \
        "$repo_root/sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs" \
        "$runtime_root/VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitTexturePrewarmer.cs"
fi

echo "Building exact runtime donors..."
unset Platform
build_errors=""
if [[ " ${eligible_projects[*]} " == *" VSEssentials "* ]]; then
    echo "  Building VSEssentials..."
    if ! dotnet build "$essentials_project" -c "$configuration" --nologo; then
        build_errors="${build_errors}VSEssentials "
    fi
fi
if [[ " ${eligible_projects[*]} " == *" VSSurvivalMod "* ]]; then
    echo "  Building VSSurvivalMod..."
    if ! dotnet build "$survival_project" -c "$configuration" --nologo; then
        build_errors="${build_errors}VSSurvivalMod "
    fi
fi

if [[ -n "$build_errors" ]]; then
    echo "Runtime donor build failed: ${build_errors}" >&2
    exit 1
fi

if [[ "${#eligible_projects[@]}" == "0" ]]; then
    echo "No runtime donors passed the compatibility gate." >&2
    exit 1
fi

printf 'Runtime donors ready for:'
printf ' %s' "${eligible_projects[@]}"
printf '\n'
