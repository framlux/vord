#!/usr/bin/env bash
# Merges the per-suite Cobertura reports, writes a human-readable summary and an HTML report, and
# fails the build when line coverage of hand-written code falls below the threshold.
#
# Usage: scripts/check-coverage.sh <directory-of-cobertura-xml> [output-directory]
#
# Generated code is excluded deliberately and is the whole reason this gate needs filters at all.
# Measured naively the repository reports ~77% and would fail on day one, because the protobuf
# message classes alone account for thousands of uncovered lines that nobody writes and nobody
# should test. Filtered to the three hand-written assemblies the same tests measure ~92%.
set -euo pipefail

REPORT_DIR="${1:?usage: check-coverage.sh <cobertura-dir> [output-dir]}"
OUTPUT_DIR="${2:-coverage-report}"
THRESHOLD="${COVERAGE_THRESHOLD:-80}"

# Hand-written assemblies only. Framlux.Vord.Grpc and Framlux.Vord.BillingGrpc are generated from
# .proto files in their entirety, so they are omitted rather than filtered class by class.
ASSEMBLY_FILTERS="+Framlux.FleetManagement.Services.Core;+Framlux.Vord.Server;+Framlux.Vord.Database"
# Belt and braces: generated protobuf types keep their own namespace even if they are ever emitted
# into one of the assemblies above.
CLASS_FILTERS="-Framlux.FleetManagement.Grpc.*;-*Reflection"

reportgenerator \
    "-reports:$REPORT_DIR/*.xml" \
    "-targetdir:$OUTPUT_DIR" \
    "-reporttypes:TextSummary;Html;Cobertura" \
    "-assemblyfilters:$ASSEMBLY_FILTERS" \
    "-classfilters:$CLASS_FILTERS"

echo
sed -n '/^Summary/,/^$/p' "$OUTPUT_DIR/Summary.txt"
grep -E "^Framlux\." "$OUTPUT_DIR/Summary.txt" || true
echo

LINE_RATE="$(python3 -c "
import sys, xml.etree.ElementTree as ET
root = ET.parse('$OUTPUT_DIR/Cobertura.xml').getroot()
print(float(root.get('line-rate', 0)) * 100)
")"

printf 'Line coverage (hand-written code): %.2f%%\n' "$LINE_RATE"
printf 'Threshold:                         %s%%\n' "$THRESHOLD"

if python3 -c "import sys; sys.exit(0 if float('$LINE_RATE') >= float('$THRESHOLD') else 1)"; then
    echo "Coverage gate passed."
else
    echo "Coverage gate FAILED: line coverage is below the $THRESHOLD% threshold." >&2
    exit 1
fi
