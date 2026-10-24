# Repository layout

One repository holds one product with independent platform builds. The clients
share domain concepts, API shapes, fixture data, and backend services, so keeping
them together makes cross-platform changes easier to review. Platform-specific
SDKs, UI code, signing, and releases remain separate.

```text
apps/
  web/             Browser / older Tizen web widget
  tizen-dotnet/    Native NUI application for Tizen 9
  android/         Experimental .NET MAUI Android client
services/
  api/             Go sports/account/profile/configuration backend
  extractor/       Separate Go extraction service
scripts/           Fixture sandbox, screenshots, package validation
tests/             Sandbox and native core tests
docs/              Setup, release instructions, real screenshots
```

Root npm scripts forward to the web workspace, so existing commands such as
`npm start` and `npm test` still work. The web app retains its Tizen-compatible
entry point and source layout inside `apps/web`; browser packaging uses an explicit
allowlist and never includes sibling apps or services.

The native imports retain independent projects instead of immediately extracting
a shared assembly from a partially completed Android port. Core tests exercise
the port's real models/catalog/ranking code, and both native clients compile in
CI. Shared libraries can be extracted after their platform differences are tested.
Go module identities are preserved to avoid breaking internal imports; they do
not require separate GitHub repositories.

The native Tizen project targets API 9. It is not the older Tizen 5.5 solution;
the web widget is. Build success is separate from native device compatibility,
Samsung signing, production authentication, and media playback acceptance.

The sanitized import's historical notes/designs are retained under
`docs/imported-native` and `docs/design/native`. Their old paths and scaffold
instructions describe the original machine, not the current monorepo setup.
