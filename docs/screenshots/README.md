# Screenshots

Real captures of the web fixture sandbox at 1920×1080, using sample content.
These are not renderings of the native Tizen NUI app.

## Browser profiles

![Browser profile picker](web-profiles.png)

## Browser catalog

![Browser catalog](web-catalog.png)

## Browser stream selection

![Browser stream picker](web-streams.png)

Regenerate these with `npm run screenshots`. Android images are captured separately
by `scripts/android-smoke.sh` after its UI renders the expected fixture catalog.
Native Tizen captures require a Tizen runtime/device.

## Android sandbox — experimental MAUI port

Actual API 28 Android emulator capture after the APK launched and rendered its
fixture catalog. This is a minimal port, not visual parity with the native TV UI.
Production login, refined card sizing, and media playback need further acceptance
work.

![Android fixture catalog](android-catalog.png)
