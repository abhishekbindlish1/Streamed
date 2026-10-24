#!/usr/bin/env bash
set -euo pipefail
native_sdk="${ANDROID_HOME:?Set ANDROID_HOME to the runner Android SDK}"
native_manager="$native_sdk/cmdline-tools/latest/bin/sdkmanager"
native_log="${RUNNER_TEMP:-/tmp}/streamed-sdk-install.log"
if [[ ! -x "$native_manager" ]]; then echo "::error::Missing sdkmanager: $native_manager"; exit 1; fi
export ANDROID_SDK_ROOT="$native_sdk"
if [[ -n "${GITHUB_ENV:-}" ]]; then echo "ANDROID_SDK_ROOT=$native_sdk" >> "$GITHUB_ENV"; fi
if [[ -n "${GITHUB_PATH:-}" ]]; then
  echo "$native_sdk/cmdline-tools/latest/bin" >> "$GITHUB_PATH"
  echo "$native_sdk/platform-tools" >> "$GITHUB_PATH"
fi
# Keep sdkmanager's result, including when yes exits on a closed input pipe.
if ! "$native_manager" --licenses < <(yes) > "$native_log" 2>&1; then
  tail -n 12 "$native_log" | sed 's/^/::error::/'
  exit 1
fi
if ! "$native_manager" "$@" > "$native_log" 2>&1; then
  cat "$native_log"
  tail -n 12 "$native_log" | sed 's/^/::error::/'
  exit 1
fi
cat "$native_log"
