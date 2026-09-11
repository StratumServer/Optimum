#!/usr/bin/env bash
# Materialise the git-ignored trees (build/, the fork checkouts, .vanilla, _ref, .baseline, .build)
# from .baseline + patches/ + sources/, offline. It mirrors scripts/bootstrap.sh steps 7 and 8
# (patch apply, sources/ overlay, closure-class fixup). bin/ and obj/ are kept, so builds stay
# incremental.
#
#   scripts/dev/worktree-bootstrap.sh [main-checkout-path]
#       inside a secondary worktree: link the shared trees from the main checkout, then materialise.
#   scripts/dev/worktree-bootstrap.sh --in-place
#       inside the main checkout after a merge or checkout changed patches/ or sources/: refuses
#       when build/ or a fork holds edits that extract-patches.sh has not written out yet.
set -euo pipefail

wt="$(git rev-parse --show-toplevel)"
main_default="$(git worktree list --porcelain | awk 'NR==1 && $1=="worktree"{print $2}')"
in_place=0
if [[ "${1:-}" == "--in-place" ]]; then
  in_place=1
  main="$wt"
  if [[ "$wt" != "$main_default" ]]; then
    echo "worktree-bootstrap: --in-place is for the main checkout ($main_default)" >&2
    exit 1
  fi
  if [[ -n "$(git status --porcelain -- patches sources)" ]]; then
    echo "worktree-bootstrap: patches/ or sources/ has uncommitted changes; commit them first" >&2
    exit 1
  fi
  bash "$wt/scripts/extract-patches.sh" >/dev/null
  if [[ -n "$(git status --porcelain -- patches sources)" ]]; then
    git status --short -- patches sources >&2
    git checkout -- patches sources
    git clean -fdq -- patches sources
    echo "worktree-bootstrap: build/ or a fork holds edits not in patches/; run extract-patches.sh and commit first" >&2
    exit 1
  fi
else
  main="${1:-$main_default}"
  if [[ "$wt" == "$main" ]]; then
    echo "worktree-bootstrap: run this inside a secondary worktree, or pass --in-place in the main checkout" >&2
    exit 1
  fi
  for d in .vanilla _ref .baseline .build; do
    if [[ ! -e "$main/$d" ]]; then
      echo "worktree-bootstrap: $main/$d is missing; run make bootstrap in the main checkout first" >&2
      exit 1
    fi
    [[ -e "$wt/$d" ]] || ln -s "$main/$d" "$wt/$d"
    # .gitignore's "name/" rules do not match a symlink, so exclude the links locally (info/exclude
    # is shared by every worktree of this repository and is never committed).
    exclude="$(git rev-parse --git-path info/exclude)"
    grep -qxF "$d" "$exclude" 2>/dev/null || echo "$d" >> "$exclude"
  done
fi

is_vanilla_project() { case "$1" in VintagestoryLib|Vintagestory) return 0 ;; *) return 1 ;; esac; }

for proj_dir in "$main"/.baseline/*/; do
  proj="$(basename "$proj_dir")"
  if is_vanilla_project "$proj"; then target="$wt/build/$proj"; else target="$wt/$proj"; fi
  mkdir -p "$target"
  find "$target" -mindepth 1 -maxdepth 1 ! -name bin ! -name obj -exec rm -rf {} +
  cp -a "$proj_dir". "$target"/
done

applied=0
failed=()
while IFS= read -r -d '' patch; do
  rel="${patch#$wt/}"
  proj="$(cut -d/ -f2 <<<"$rel")"
  args=(--whitespace=nowarn)
  if is_vanilla_project "$proj"; then args+=(--directory=build); fi
  if (cd "$wt" && git apply "${args[@]}" "$patch" 2>/dev/null) \
     || (cd "$wt" && git apply "${args[@]}" -p0 "$patch" 2>/dev/null); then
    applied=$((applied + 1))
  else
    failed+=("$rel")
  fi
done < <(find "$wt/patches" -type f -name '*.patch' -not -path '*/runtime/*' -print0 | sort -z)

while IFS= read -r -d '' src; do
  rel="${src#$wt/sources/}"
  top="$(cut -d/ -f1 <<<"$rel")"
  case "$top" in lang|shaders|shaderincludes) continue ;; esac
  if is_vanilla_project "$top"; then target="$wt/build/$rel"; else target="$wt/$rel"; fi
  mkdir -p "$(dirname "$target")"
  cp -f "$src" "$target"
done < <(find "$wt/sources" -type f -print0)

perl "$wt/scripts/fix-closure-class.pl" \
  "$wt/build/VintagestoryLib/Vintagestory.Client/ScreenManager.cs" \
  "$wt/build/VintagestoryLib/Vintagestory.Client/GuiScreenRunningGame.cs"

touch "$wt/.bootstrap-complete"
echo "worktree-bootstrap: $wt materialised from $main ($applied patches applied, ${#failed[@]} failed)"
if ((${#failed[@]})); then
  printf '  FAILED %s\n' "${failed[@]}" >&2
  exit 1
fi
