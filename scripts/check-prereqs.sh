#!/usr/bin/env bash
# Checks for the tools the bootstrap/packaging scripts need.
# Reports only — never installs. Exit 0 if all required tools are present,
# exit 1 if any required tool is missing (optional tools never fail the run).
set -uo pipefail

red()    { printf '\033[31m%s\033[0m' "$1"; }
green()  { printf '\033[32m%s\033[0m' "$1"; }
yellow() { printf '\033[33m%s\033[0m' "$1"; }

missing_required=0
missing_optional=0

# name | required(1/0) | used by | hint
checks=(
  "dotnet|1|bootstrap.sh, build|.NET SDK 10 — https://dotnet.microsoft.com/download"
  "git|1|bootstrap.sh, extract-patches.sh, package-macos.ps1|apt-get install git"
  "perl|1|bootstrap.sh, extract-patches.sh|apt-get install perl"
  "python3|1|bootstrap.sh|apt-get install python3"
  "curl|1|bootstrap.sh, package-*.ps1|apt-get install curl"
  "tar|1|bootstrap.sh, package-*.ps1|apt-get install tar"
  "unzip|0|bootstrap.sh (zip archives; python3 fallback exists)|apt-get install unzip"
  "pwsh|1|package-linux.ps1, package-macos.ps1, package.ps1|apt-get install powershell  (or: snap install powershell --classic)"
  "ilspycmd|0|bootstrap.sh (auto-installs via dotnet tool if missing)|dotnet tool install -g ilspycmd"
  "chmod|1|package-linux.ps1|coreutils"
  "make|0|package-macos.ps1 (.dmg on Linux via libdmg-hfsplus)|apt-get install make"
  "cmake|0|package-macos.ps1 (.dmg on Linux via libdmg-hfsplus)|apt-get install cmake"
  "mkisofs|0|package-macos.ps1 (.dmg on Linux; genisoimage also works)|apt-get install cdrtools  (or genisoimage)"
  "innoextract|0|package.ps1 (Windows package on Linux/macOS hosts)|apt-get install innoextract"
)

printf '%s\n\n' "$(yellow 'Optimum prerequisite check (report only — nothing is installed)')"
printf '%-14s %-10s %s\n' "TOOL" "STATUS" "USED BY"
printf '%-14s %-10s %s\n' "----" "------" "-------"

for entry in "${checks[@]}"; do
  IFS='|' read -r name required used hint <<< "$entry"
  if command -v "$name" >/dev/null 2>&1; then
    printf '%-14s %s%-8s %s\n' "$name" "$(green OK)" "" "$used"
  else
    if [[ "$required" == "1" ]]; then
      printf '%-14s %s%-3s %s\n' "$name" "$(red MISSING)" "" "$used"
      printf '               %s %s\n' "$(red '→ required.')" "$hint"
      missing_required=$((missing_required + 1))
    else
      printf '%-14s %s%-2s %s\n' "$name" "$(yellow optional)" "" "$used"
      printf '               %s %s\n' "$(yellow '→ install only if needed:')" "$hint"
      missing_optional=$((missing_optional + 1))
    fi
  fi
done

echo
if [[ "$missing_required" -gt 0 ]]; then
  printf '%s\n' "$(red "$missing_required required tool(s) missing — install them before bootstrap/packaging.")"
  exit 1
fi
if [[ "$missing_optional" -gt 0 ]]; then
  printf '%s\n' "$(yellow "All required tools present. $missing_optional optional tool(s) missing (some package targets will be skipped).")"
else
  printf '%s\n' "$(green 'All tools present.')"
fi
exit 0
