#!/usr/bin/env bash
# Tests batched patch syntax validation and batched source patch application
# with atomic per-patch fallback in scripts/bootstrap.sh and scripts/validate-patch-syntax.sh.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -rf "$TEST_ROOT"' EXIT

unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE

# 1. Assert coupling with bootstrap.sh, bootstrap.ps1, and validate-patch-syntax.sh
grep -qF 'git_apply_optimum_patch_batch()' "$REPO_ROOT/scripts/bootstrap.sh" || {
  echo "Expected git_apply_optimum_patch_batch() in scripts/bootstrap.sh" >&2
  exit 1
}
grep -qF 'git_apply_optimum_patch_batch "$patch_batch_dir_arg" applied failed "${patch_batch[@]}"' "$REPO_ROOT/scripts/bootstrap.sh" || {
  echo "Expected batched patch apply invocation in scripts/bootstrap.sh" >&2
  exit 1
}
grep -qF 'git apply --stat "${patches[@]}"' "$REPO_ROOT/scripts/validate-patch-syntax.sh" || {
  echo "Expected batched git apply --stat in scripts/validate-patch-syntax.sh" >&2
  exit 1
}
grep -qF 'function Invoke-GitApplyOptimumPatchBatch' "$REPO_ROOT/scripts/bootstrap.ps1" || {
  echo "Expected Invoke-GitApplyOptimumPatchBatch in scripts/bootstrap.ps1" >&2
  exit 1
}
grep -qF 'function Test-PatchSyntaxBatch' "$REPO_ROOT/scripts/bootstrap.ps1" || {
  echo "Expected Test-PatchSyntaxBatch in scripts/bootstrap.ps1" >&2
  exit 1
}

# Extract git_apply_optimum_patch and git_apply_optimum_patch_batch from bootstrap.sh
eval "$(awk '/^git_apply_optimum_patch\(\) \{/,/^\}/' "$REPO_ROOT/scripts/bootstrap.sh")"
eval "$(awk '/^git_apply_optimum_patch_batch\(\) \{/,/^\}/' "$REPO_ROOT/scripts/bootstrap.sh")"

# 2. Verify batched apply equals serial per-patch apply across root and --directory=build,
#    including git invocation count and ordered same-file fallback.
setup_tree() {
  local dir="$1"
  mkdir -p "$dir/VintagestoryApi" "$dir/build/VintagestoryLib"
  git init -q "$dir"
  cat > "$dir/VintagestoryApi/ApiFile.cs" <<'EOF'
public class ApiFile
{
    public int ValueA() => 1;
}
EOF
  cat > "$dir/VintagestoryApi/OtherFile.cs" <<'EOF'
public class OtherFile
{
    public int ValueB() => 2;
}
EOF
  cat > "$dir/build/VintagestoryLib/LibFile.cs" <<'EOF'
public class LibFile
{
    public string Name() => "vanilla";
}
EOF
}

patch_dir="$TEST_ROOT/patches"
mkdir -p "$patch_dir/VintagestoryApi" "$patch_dir/VintagestoryLib"

cat > "$patch_dir/VintagestoryApi/01-First.patch" <<'EOF'
--- a/VintagestoryApi/ApiFile.cs
+++ b/VintagestoryApi/ApiFile.cs
@@ -1,4 +1,4 @@
 public class ApiFile
 {
-    public int ValueA() => 1;
+    public int ValueA() => 10;
 }
EOF

cat > "$patch_dir/VintagestoryApi/02-Second.patch" <<'EOF'
--- a/VintagestoryApi/OtherFile.cs
+++ b/VintagestoryApi/OtherFile.cs
@@ -1,4 +1,4 @@
 public class OtherFile
 {
-    public int ValueB() => 2;
+    public int ValueB() => 20;
 }
EOF

cat > "$patch_dir/VintagestoryLib/01-Lib.patch" <<'EOF'
--- a/VintagestoryLib/LibFile.cs
+++ b/VintagestoryLib/LibFile.cs
@@ -1,4 +1,4 @@
 public class LibFile
 {
-    public string Name() => "vanilla";
+    public string Name() => "optimum";
 }
EOF

# Validate patch syntax in batch mode
bash "$REPO_ROOT/scripts/validate-patch-syntax.sh" "$patch_dir" >/dev/null

# Also test that validate-patch-syntax.sh catches a bad patch via fallback
bad_syntax_dir="$TEST_ROOT/bad-syntax"
cp -R "$patch_dir" "$bad_syntax_dir"
cat > "$bad_syntax_dir/VintagestoryApi/99-Corrupt.patch" <<'EOF'
--- a/VintagestoryApi/ApiFile.cs
+++ b/VintagestoryApi/ApiFile.cs
@@ -1,99 +1,99 @@
+broken hunk header
EOF
if bash "$REPO_ROOT/scripts/validate-patch-syntax.sh" "$bad_syntax_dir" >/dev/null 2>&1; then
  echo "validate-patch-syntax.sh should reject corrupt patch" >&2
  exit 1
fi

serial_tree="$TEST_ROOT/serial"
batch_tree="$TEST_ROOT/batch"
setup_tree "$serial_tree"
setup_tree "$batch_tree"

(
  cd "$serial_tree"
  git_apply_optimum_patch "$patch_dir/VintagestoryApi/01-First.patch" ""
  git_apply_optimum_patch "$patch_dir/VintagestoryApi/02-Second.patch" ""
  git_apply_optimum_patch "$patch_dir/VintagestoryLib/01-Lib.patch" "--directory=build"
)

# Count git apply calls during batched application using a PATH wrapper
git_log="$TEST_ROOT/git-apply.log"
real_git="$(command -v git)"
mkdir -p "$TEST_ROOT/bin"
cat > "$TEST_ROOT/bin/git" <<EOF
#!/usr/bin/env bash
if [[ "\${1:-}" == "apply" ]]; then
  echo "git \$*" >> "$git_log"
fi
exec "$real_git" "\$@"
EOF
chmod +x "$TEST_ROOT/bin/git"

(
  cd "$batch_tree"
  repo_root="$batch_tree"
  applied=0
  failed=()
  PATH="$TEST_ROOT/bin:$PATH"
  git_apply_optimum_patch_batch "" applied failed \
    "$patch_dir/VintagestoryApi/01-First.patch" \
    "$patch_dir/VintagestoryApi/02-Second.patch"
  git_apply_optimum_patch_batch "--directory=build" applied failed \
    "$patch_dir/VintagestoryLib/01-Lib.patch"
  [[ "$applied" -eq 3 ]] || { echo "Expected 3 applied patches, got $applied" >&2; exit 1; }
  [[ "${#failed[@]}" -eq 0 ]] || { echo "Expected 0 failed patches" >&2; exit 1; }
)

cmp -s "$serial_tree/VintagestoryApi/ApiFile.cs" "$batch_tree/VintagestoryApi/ApiFile.cs" || {
  echo "Batched ApiFile.cs differs from serial apply" >&2
  exit 1
}
cmp -s "$serial_tree/VintagestoryApi/OtherFile.cs" "$batch_tree/VintagestoryApi/OtherFile.cs" || {
  echo "Batched OtherFile.cs differs from serial apply" >&2
  exit 1
}
cmp -s "$serial_tree/build/VintagestoryLib/LibFile.cs" "$batch_tree/build/VintagestoryLib/LibFile.cs" || {
  echo "Batched LibFile.cs differs from serial apply" >&2
  exit 1
}
apply_calls="$(wc -l < "$git_log" | tr -d ' ')"
[[ "$apply_calls" -eq 2 ]] || {
  echo "Expected 2 batched git apply invocations for 2 directory groups, got $apply_calls" >&2
  exit 1
}

# 3. Verify atomic batch failure falls back to per-patch apply, preserves -p0 fallback,
#    and reports the exact failing patch without double-applying earlier patches.
fallback_tree="$TEST_ROOT/fallback"
setup_tree "$fallback_tree"
cat > "$fallback_tree/VintagestoryApi/ThirdFile.cs" <<'EOF'
public class ThirdFile
{
    public int Flag() => 0;
}
EOF

cat > "$patch_dir/VintagestoryApi/03-P0Fallback.patch" <<'EOF'
--- VintagestoryApi/ThirdFile.cs
+++ VintagestoryApi/ThirdFile.cs
@@ -1,4 +1,4 @@
 public class ThirdFile
 {
-    public int Flag() => 0;
+    public int Flag() => 1;
 }
EOF

cat > "$patch_dir/VintagestoryApi/04-Conflict.patch" <<'EOF'
--- a/VintagestoryApi/ApiFile.cs
+++ b/VintagestoryApi/ApiFile.cs
@@ -1,4 +1,4 @@
 public class ApiFile
 {
-    public int NonExistent() => 999;
+    public int NonExistent() => 1000;
 }
EOF

(
  cd "$fallback_tree"
  repo_root="$TEST_ROOT"
  applied=0
  failed=()
  git_apply_optimum_patch_batch "" applied failed \
    "$patch_dir/VintagestoryApi/01-First.patch" \
    "$patch_dir/VintagestoryApi/02-Second.patch" \
    "$patch_dir/VintagestoryApi/03-P0Fallback.patch" \
    "$patch_dir/VintagestoryApi/04-Conflict.patch" >/dev/null
  [[ "$applied" -eq 3 ]] || { echo "Expected 3 applied patches in fallback path, got $applied" >&2; exit 1; }
  [[ "${#failed[@]}" -eq 1 ]] || { echo "Expected 1 failed patch in fallback path, got ${#failed[@]}" >&2; exit 1; }
  [[ "${failed[0]}" == "patches/VintagestoryApi/04-Conflict.patch" ]] || {
    echo "Unexpected failed patch entry: ${failed[0]}" >&2
    exit 1
  }
  grep -qF 'public int Flag() => 1;' "$fallback_tree/VintagestoryApi/ThirdFile.cs" || {
    echo "Expected -p0 fallback patch to be applied during batch fallback" >&2
    exit 1
  }
)

echo "Bootstrap patch batching test passed."
