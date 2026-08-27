#!/usr/bin/env bash
# Runs one TUnit suite under coverlet and writes a Cobertura report.
#
# Usage: scripts/run-tests-with-coverage.sh <test-project-csproj> <output-cobertura-xml>
#
# The suites are self-contained executables, so the runtime-identifier folder under bin/ varies by
# host (osx-arm64 locally, linux-x64 on CI). The instrumentation directory is resolved by glob
# rather than hard-coded, and `dotnet run --no-build` is used as the target so this stays in step
# with how the workflows already invoke the suites.
set -euo pipefail

PROJECT="${1:?usage: run-tests-with-coverage.sh <csproj> <output.xml>}"
OUTPUT="${2:?usage: run-tests-with-coverage.sh <csproj> <output.xml>}"
CONFIGURATION="${CONFIGURATION:-Release}"

PROJECT_DIR="$(dirname "$PROJECT")"
BIN_DIR="$(find "$PROJECT_DIR/bin/$CONFIGURATION" -maxdepth 2 -type d -name '*-*' | head -1)"

if [ -z "$BIN_DIR" ]; then
    echo "error: no build output under $PROJECT_DIR/bin/$CONFIGURATION — build first" >&2
    exit 1
fi

mkdir -p "$(dirname "$OUTPUT")"

# Test assemblies and third-party test infrastructure are instrumented but not reported on; the
# assembly filters in scripts/check-coverage.sh decide what actually counts.
coverlet "$BIN_DIR" \
    --target "dotnet" \
    --targetargs "run --project $PROJECT -c $CONFIGURATION --no-build" \
    --format cobertura \
    --output "$OUTPUT" \
    --exclude "[*Test*]*" \
    --exclude "[TUnit*]*" \
    --exclude "[NSubstitute*]*" \
    --exclude "[shared]*"
