#!/usr/bin/env bash

# Run a Git command against a cloned working tree without relying on Git's
# current-directory discovery. This also makes the command immune to an
# inherited GIT_DIR selecting a different repository.
optimum_git_in_clone() {
  local clone_dir="${1:?clone directory is required}"
  shift
  git --git-dir="$clone_dir/.git" --work-tree="$clone_dir" "$@"
}
