#!/usr/bin/env bash
set -euo pipefail
native_dotnet_root="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
# Workload installation can skip signature verification on Linux. Validate the
# retained package archives explicitly before executing their build tools.
find "$native_dotnet_root/packs" "$native_dotnet_root/library-packs" "$native_dotnet_root/template-packs" \
  -type f -name '*.nupkg' -print0 | xargs -0 -r dotnet nuget verify --all --verbosity quiet
