#!/usr/bin/env bash
set -eu
if (set -o pipefail 2>/dev/null); then
    set -o pipefail
fi

# sort -z is GNU coreutils; macOS sort lacks it. Fall back to plain sort
# (patch filenames have no embedded newlines, so null-delimiting is
# cosmetic and not required for correctness).
if echo | sort -z 2>/dev/null; then
    sort_z() { sort -z; }
else
    sort_z() { sort; }
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
vanilla_dir="${VANILLA_DIR:-$repo_root/.vanilla/win-x64/vintagestory}"
configuration="${CONFIGURATION:-Release}"
runtime_root="$repo_root/.build/runtime-donors"
contracts_dll="$repo_root/bin/$configuration/net10.0/Optimum.Api.Contracts.dll"
game_content_dll="$repo_root/bin/$configuration/net10.0/Optimum.GameContent.dll"

normalize_lf() {
    find "$1" -type f -name '*.patch' -print0 |
        while IFS= read -r -d '' file; do perl -0pi -e 's/\r\n/\n/g; s/\r/\n/g' "$file"; done
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
    "$vanilla_dir/VintagestoryAPI.dll" \
    "$vanilla_dir/Mods/VSEssentials.dll" \
    "$vanilla_dir/Mods/VSEssentials.pdb" \
    "$vanilla_dir/Mods/VSCreativeMod.dll" \
    "$vanilla_dir/Mods/VSSurvivalMod.dll" \
    "$vanilla_dir/Mods/VSSurvivalMod.pdb" \
    "$contracts_dll" \
    "$game_content_dll"; do
    if [[ ! -f "$required" ]]; then
        echo "Required runtime-donor input not found: $required" >&2
        exit 1
    fi
done

# This directory contains generated source only. It is always reconstructed
# from the user's exact, owned game assemblies.
rm -rf -- "$runtime_root"
mkdir -p "$runtime_root"

decompile_mod() {
    local project="$1"
    local assembly="$2"
    local output="$runtime_root/$project"

    echo "Decompiling exact $project runtime donor..."
    local -a reference_args=()
    local reference_dir
    for reference_dir in "$vanilla_dir" "$vanilla_dir/Lib" "$vanilla_dir/Mods"; do
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
    VANILLA_HINT_ROOT="$vanilla_dir/" perl -0pi -e '
        s#<HintPath>\.vanilla/win-x64/vintagestory/#<HintPath>$ENV{VANILLA_HINT_ROOT}#g;
        s#<LangVersion>\d+\.\d+</LangVersion>#<LangVersion>preview</LangVersion>#g;
        s#</PropertyGroup>#  <Nullable>disable</Nullable>\n    <NoWarn>\$(NoWarn);0618;8632;0420;0649;0169;9193;9113</NoWarn>\n  </PropertyGroup>#;
    ' "$project_file"
}

decompile_mod \
    "VSEssentials" \
    "$vanilla_dir/Mods/VSEssentials.dll"
decompile_mod \
    "VSSurvivalMod" \
    "$vanilla_dir/Mods/VSSurvivalMod.dll"

add_reference() {
    local project_file="$1"
    local include="$2"
    local hint_path="$3"
    PROJECT_INCLUDE="$include" PROJECT_HINT_PATH="$hint_path" perl -0pi -e '
        s#<ItemGroup>#<ItemGroup>\n    <Reference Include="$ENV{PROJECT_INCLUDE}">\n      <HintPath>$ENV{PROJECT_HINT_PATH}</HintPath>\n      <Private>false</Private>\n    </Reference>#;
    ' "$project_file"
}

set_reference_hint_path() {
    local project_file="$1"
    local include="$2"
    local hint_path="$3"
    PROJECT_INCLUDE="$include" PROJECT_HINT_PATH="$hint_path" perl -0pi -e '
        s#<Reference Include="$ENV{PROJECT_INCLUDE}"\s*/>#<Reference Include="$ENV{PROJECT_INCLUDE}">\n      <HintPath>$ENV{PROJECT_HINT_PATH}</HintPath>\n      <Private>false</Private>\n    </Reference>#;
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
add_reference "$essentials_project" "Optimum.Api.Contracts" "$contracts_dll"
add_reference "$essentials_project" "Optimum.GameContent" "$game_content_dll"
add_reference "$survival_project" "Optimum.Api.Contracts" "$contracts_dll"
set_reference_hint_path \
    "$survival_project" \
    "VSCreativeMod" \
    "$vanilla_dir/Mods/VSCreativeMod.dll"
exclude_compile_items "$survival_project" \
    "Vintagestory/GameContent/ModSystemVillagerDebug.cs" \
    "Vintagestory/ServerMods/ChiselBlockBulkSetMaterial.cs" \
    "Vintagestory/ServerMods/UpgradeTasks.cs"

resolve_references() {
    local project_file="$1"
    VANILLA_DIR="$vanilla_dir" perl -0pi -e '
        my @dirs = ("$ENV{VANILLA_DIR}/Lib", $ENV{VANILLA_DIR}, "$ENV{VANILLA_DIR}/Mods");
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
                qq{<Reference Include="$name">\n      <HintPath>$found</HintPath>\n      <Private>false</Private>\n    </Reference>};
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
                    qq{<Reference Include="$name">\n      <HintPath>$found</HintPath>\n      <Private>false</Private>\n    </Reference>};
                } else {
                    qq{<Reference Include="$name">\n      <HintPath>$hint</HintPath>\n    </Reference>};
                }
            }
        }ge;
    ' "$project_file"
}
resolve_references "$essentials_project"
resolve_references "$survival_project"

eligible_projects=()
for project in VSEssentials VSSurvivalMod; do
    project_ready=1
    while IFS= read -r -d '' patch; do
        if ! git -C "$repo_root" apply --check \
            --directory=".build/runtime-donors" \
            --whitespace=nowarn \
            "$patch"; then
            echo "Runtime donor unavailable: $project requires a patch refresh for $(basename "$patch")." >&2
            project_ready=0
            break
        fi
    done < <(find "$repo_root/patches/runtime/$project" -name '*.patch' -print0 | sort_z)

    if [[ "$project_ready" == "1" ]]; then
        while IFS= read -r -d '' patch; do
            git -C "$repo_root" apply \
                --directory=".build/runtime-donors" \
                --whitespace=nowarn \
                "$patch"
        done < <(find "$repo_root/patches/runtime/$project" -name '*.patch' -print0 | sort_z)
        eligible_projects+=("$project")
    fi
done

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
