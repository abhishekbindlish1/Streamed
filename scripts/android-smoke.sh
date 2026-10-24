#!/usr/bin/env bash
set -euo pipefail
native_adb="${ADB:-adb}"
native_package=com.streamed.tv.android.sandbox
native_output="${SCREENSHOT_DIR:-docs/screenshots}"
mkdir -p "$native_output"
native_ui_file="$(mktemp)"
trap 'rm -f "$native_ui_file"' EXIT
"$native_adb" shell input keyevent 82
"$native_adb" shell am force-stop "$native_package"
"$native_adb" shell monkey -p "$native_package" -c android.intent.category.LAUNCHER 1
for native_attempt in $(seq 1 30); do
  "$native_adb" shell rm -f /sdcard/streamed-sandbox-ui.xml
  "$native_adb" shell uiautomator dump /sdcard/streamed-sandbox-ui.xml > /dev/null
  if ! "$native_adb" exec-out cat /sdcard/streamed-sandbox-ui.xml > "$native_ui_file" 2>/dev/null; then
    sleep 1
    continue
  fi
  # StreamRanker orders the two live cricket fixtures by title: Metro is first.
  if grep -q 'SANDBOX' "$native_ui_file" && grep -q 'Metro Strikers' "$native_ui_file"; then
    "$native_adb" exec-out screencap -p > "$native_output/android-catalog.png"
    echo 'Android sandbox launched and rendered fixture matches; captured its real UI.'
    exit 0
  fi
  sleep 1
done
"$native_adb" exec-out screencap -p > "$native_output/android-failure.png"
"$native_adb" logcat -d -s AndroidRuntime:E
echo 'Android sandbox did not render the expected fixture catalog.' >&2
exit 1
