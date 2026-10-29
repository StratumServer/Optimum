#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
status_source="$repo_root/sources/VSEssentials/Systems/OptimumStatus.cs"
status_test="$repo_root/Optimum.Tests/OptimumStatusTests.cs"

if grep -Eq 'TyronThreadPool\.SetMaxThreads(WorkerBefore|WorkerAfter|IoBefore|IoAfter|Result)' "$status_source"; then
    echo "OptimumStatus must not statically depend on Optimum-only TyronThreadPool telemetry members." >&2
    exit 1
fi

if ! grep -Fq 'System.Threading.ThreadPool.GetMaxThreads' "$status_source" \
    || ! grep -Fq 'GetProperty("SetMaxThreadsResult")' "$status_source"; then
    echo "OptimumStatus must report effective limits and optionally read patched API telemetry." >&2
    exit 1
fi

if ! grep -Fq 'StatusReportsEffectiveThreadPoolLimitsAndOptionalOptimumMeasurement' "$status_test"; then
    echo "OptimumStatusTests must cover the donor-compatible thread-pool summary." >&2
    exit 1
fi

echo "Runtime donor API compatibility check passed."
