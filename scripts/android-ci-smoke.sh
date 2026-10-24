#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

native_sdk="${ANDROID_HOME:?Set ANDROID_HOME to the Android SDK}"
native_adb="$native_sdk/platform-tools/adb"
native_output="${SCREENSHOT_DIR:-test-results/android}"
native_port="${ANDROID_EMULATOR_PORT:-5554}"
native_avd="${1:?Pass the name of a prepared Android AVD}"
native_apk=apps/android/bin/Debug/net10.0-android/android-x64/com.streamed.tv.android.sandbox-Signed.apk
native_pairing_apk=apps/android/bin/Debug/net10.0-android/android-x64/com.streamed.tv.android.sandbox.pairing-Signed.apk
native_emulator_pid=
native_server_pid=
native_stage='start fixture server'
export ANDROID_SERIAL="emulator-$native_port"
mkdir -p "$native_output"

cleanup() {
  native_result=$?
  trap - EXIT
  if (( native_result != 0 )); then
    echo "::error::Android test infrastructure failed during: $native_stage (exit $native_result)"
    timeout 10 "$native_adb" logcat -d -s AndroidRuntime:E StreamedTv:I ExoPlayerImpl:W > "$native_output/android-runtime.log" 2>&1 || true
    for native_log in emulator sandbox android-runtime; do
      if [[ -f "$native_output/$native_log.log" ]]; then
        tail -n 60 "$native_output/$native_log.log"
      fi
    done
    if [[ -f "$native_output/emulator.log" ]]; then
      grep -Ei 'error|fatal|panic|failed|not enough|no space' "$native_output/emulator.log" | tail -n 15 | sed 's/^/::error::/' || true
      # Keep bounded, credential-free emulator startup context visible in the
      # check annotations even when Actions artifact downloads are unavailable.
      python3 - "$native_output/emulator.log" <<'PY'
from pathlib import Path
import sys
lines = Path(sys.argv[1]).read_text(errors='replace').splitlines()
for label, section in [('startup', lines[:45]), ('shutdown', lines[-45:])]:
    message = '\n'.join(section).replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    print('::notice::Android emulator ' + label + ': ' + message)
PY
    fi
  fi
  if [[ -n "$native_emulator_pid" ]]; then
    kill "$native_emulator_pid" 2>/dev/null || true
    for native_attempt in $(seq 1 10); do
      if ! kill -0 "$native_emulator_pid" 2>/dev/null; then break; fi
      sleep 1
    done
    if kill -0 "$native_emulator_pid" 2>/dev/null; then
      kill -KILL "$native_emulator_pid" 2>/dev/null || true
    fi
    wait "$native_emulator_pid" 2>/dev/null || true
  fi
  if [[ -n "$native_server_pid" ]]; then
    kill "$native_server_pid" 2>/dev/null || true
    wait "$native_server_pid" 2>/dev/null || true
  fi
  exit "$native_result"
}
trap cleanup EXIT

if "$native_adb" devices | awk 'NR > 1 {print $1}' | grep -qx "$ANDROID_SERIAL"; then
  echo "::error::Refusing to replace an existing emulator on port $native_port"
  exit 1
fi
if [[ "${ANDROID_FIXTURE_SERVER_RUNNING:-false}" != true ]]; then
  SANDBOX_PAIRING_FIXTURE=true node scripts/sandbox.js > "$native_output/sandbox.log" 2>&1 &
  native_server_pid=$!
fi
curl --fail --retry 10 --retry-connrefused --retry-delay 1 http://127.0.0.1:8080/health |
  node -e 'let data=""; process.stdin.on("data",x=>data+=x).on("end",()=>{if(JSON.parse(data).mode!=="sandbox")process.exit(1)})'
if [[ -n "$native_server_pid" ]] && ! kill -0 "$native_server_pid" 2>/dev/null; then
  echo '::error::The fixture server did not start'
  exit 1
fi

native_acceleration=auto
if [[ ! -r /dev/kvm || ! -w /dev/kvm ]]; then native_acceleration=off; fi
native_stage='boot Android emulator'
"$native_sdk/emulator/emulator" -avd "$native_avd" -port "$native_port" \
  -read-only -accel "$native_acceleration" -no-window -no-audio -no-boot-anim \
  -no-snapshot -gpu "${ANDROID_EMULATOR_GPU:-software}" -feature -Vulkan \
  > "$native_output/emulator.log" 2>&1 &
native_emulator_pid=$!
native_deadline=$((SECONDS + ${ANDROID_BOOT_TIMEOUT:-360}))
native_booted=false
while (( SECONDS < native_deadline )); do
  if ! kill -0 "$native_emulator_pid" 2>/dev/null; then
    echo '::error::The Android emulator exited before boot completed'
    exit 1
  fi
  native_boot="$(timeout 5 "$native_adb" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r' || true)"
  # On newer TV images sys.boot_completed can be published before adbd and
  # Package Manager finish restarting. Do not issue input/install during that gap.
  if [[ "$native_boot" == 1 ]] && timeout 10 "$native_adb" shell pm path android 2>/dev/null | grep -q '^package:'; then
    sleep 2
    if [[ "$(timeout 5 "$native_adb" get-state 2>/dev/null || true)" == device ]]; then
      native_booted=true
      break
    fi
  fi
  sleep 2
done
if [[ "$native_booted" != true ]]; then
  echo '::error::The Android emulator did not complete boot within the deadline'
  exit 1
fi
echo "Android emulator booted with acceleration=$native_acceleration"
native_stage='wake and configure emulator'
"$native_adb" shell input keyevent 224
"$native_adb" shell input keyevent 82
for native_setting in window_animation_scale transition_animation_scale animator_duration_scale; do
  "$native_adb" shell settings put global "$native_setting" 0
done
native_stage='install sandbox APK'
timeout 180 "$native_adb" install -r "$native_apk"
native_stage='sandbox remote playback checks'
ADB="$native_adb" SCREENSHOT_DIR="$native_output" bash scripts/android-smoke.sh
native_stage='install pairing fixture APK'
timeout 180 "$native_adb" install -r "$native_pairing_apk"
native_stage='pairing UI checks'
ADB="$native_adb" SCREENSHOT_DIR="$native_output" bash scripts/android-smoke.sh --pairing
