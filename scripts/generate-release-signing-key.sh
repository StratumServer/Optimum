#!/usr/bin/env bash
# Generates the ECDSA P-256 key that signs release manifests for the one-click update.
#
#   bash scripts/generate-release-signing-key.sh <output-dir>
#
# Writes <output-dir>/optimum-release-signing.pem (PRIVATE, keep it out of the repository) and prints:
#   1. the base64 public key to paste into OptimumUpdateTrust.ManifestPublicKey
#      (sources/VintagestoryLib/Optimum/Update/OptimumUpdateTrust.cs), and
#   2. the command that stores the private key as the OPTIMUM_RELEASE_SIGNING_KEY repository secret.
# The release workflow signs optimum-manifest-<rid>.json with that secret. Until the public key is set in the
# source and the secret exists, releases carry no signature and the in-game update button stays off.
set -euo pipefail

out="${1:-}"
if [ -z "$out" ]; then
  echo "usage: $0 <output-dir>" >&2
  exit 2
fi
mkdir -p "$out"
pem="$out/optimum-release-signing.pem"
if [ -e "$pem" ]; then
  echo "refusing to overwrite $pem" >&2
  exit 1
fi

umask 077
openssl ecparam -name prime256v1 -genkey -noout -out "$pem"
public="$(openssl ec -in "$pem" -pubout -outform DER 2>/dev/null | openssl base64 -A)"

echo "Private key written to: $pem"
echo
echo "Public key (paste into OptimumUpdateTrust.ManifestPublicKey):"
echo "$public"
echo
echo "Store the private key as a repository secret, then delete the local file:"
echo "  gh secret set OPTIMUM_RELEASE_SIGNING_KEY --repo StratumServer/Optimum < \"$pem\""
