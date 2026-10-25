# Samsung signing and distribution

A build is signed once when it is produced. Downloading or installing the same
signed package does not require a new signature for each person. Development
device authorization and the publisher's update identity are separate concerns.

## What the working TPK establishes

The uploaded `com.streamed.tv.dotnet-1.13.33.tpk.zip` is the TPK archive itself,
with an extra `.zip` suffix. Its SHA-256 is
`bb52a5fb5819f9672ef316b60adc74518955c11f0a437adfa878f79b06a4d76f`.
The owner reports successfully installing it on a TV in Developer Mode.

Both embedded signer certificates exactly match the public test certificates
shipped in `Tizen.NET.Sdk` 1.2.1:

| Role | SDK fixture | Certificate expires |
| --- | --- | --- |
| Author | `author_test.p12` | January 1, 2019 |
| Distributor | `tizen-distributor-signer.p12` | October 27, 2022 |

This package does not establish that an owner-specific Samsung signing profile
was used. The successful TV installation is useful evidence about that device's
development setup. It does not establish that all TV models or the current
Samsung emulator accept these certificates. The current CI package has different,
date-valid SDK signing certificates, but the TV 10 emulator rejects its chain
with installer error `118 / -12`. Its installation problem remains unresolved.

The uploaded manifest declares API 6.0, while its dependency metadata targets
Tizen 9.0. Installation alone does not prove compatibility with either an older
runtime or the pictured Tizen 5.5 TV. The current project consistently targets
API 9; use the web client for the existing Tizen 5.5 compatibility path.

Only public signing metadata was needed for this comparison. The uploaded binary,
SDK test keys, and signing passwords are not added to the repository or releases.

## Automatic signing for development releases

Use one owned author identity across releases. Configure its author/distributor
profile once using the four [repository secrets documented in the native
README](../apps/tizen-dotnet/README.md). The existing builder and workflows then
sign each new build automatically. Consumers download the resulting TPK; they do
not run the signing process themselves.

[Samsung's certificate workflow](https://developer.samsung.com/smarttv/develop/getting-started/setting-up-sdk/creating-certificates.html)
authorizes development targets through their DUIDs in a distributor certificate.
One signed package can be shared with the targets its profile authorizes. Adding
a target can require updating that distributor certificate and producing a new
package. This is per target registration, not per download or every release.
Certificate expiry and renewal also need maintenance.

Keep the same author identity when extending device authorization or renewing a
profile. Moving from the old shared SDK test author to an owned author can
require a one-time uninstall/reinstall on an existing TV, which can remove local
app data. Do not use public SDK test keys as a release publisher identity.

The default builder currently generates a disposable author if no profile is
provided. That path is for build checks and does not provide a stable update
identity. The uploaded TPK contains public certificates, not the private profile
needed to configure owned release signing. CI support is implemented; a working,
stable Samsung profile and emulator acceptance still need to be established.

## Distribution to a wider audience

Developer Mode still requires setup on each TV. GitHub Releases can deliver the
signed installer, but cannot turn a development profile into universal device
authorization. This repository is also private, so its downloads require access.

For ordinary TV installation and updates, pursue Samsung Apps TV distribution
through the [Seller Office](https://developer.samsung.com/tv-seller-office/faq/application-registration.html).
For an approved application, the store manages installation and updates. Confirm
the native app's eligibility and target model support with Samsung before
promising that route; no store submission has been made for this project.

For a development group, the practical path is an owned, persistent profile,
automatic CI signing, and a shared tested installer for authorized targets. Broad
distribution needs the store process or a separately validated sideloading
strategy across the intended TVs.
