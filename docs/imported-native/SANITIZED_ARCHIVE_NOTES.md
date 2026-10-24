# Sanitized source archive

Contains the Tizen TV app, Android app, backend, and backend-extractor from streamed-dotnet-tv.

Hardcoded API credentials and the shared JWT signing key were replaced with REDACTED_CONFIGURE_BEFORE_USE. Original project files were not changed.

Redacted files:
- backend/deploy.sh
- backend-extractor/deploy.sh
- src/StreamedTv/Services/StreamExtractorService.cs
- src/StreamedTv.Android/Services/StreamExtractorService.cs

Excluded: build outputs, packaged apps, compiled extractor executable, Git history, local assistant settings, environment files, signing-key files, and caches.

This is a source archive, not a tested or deployable build. Supply fresh credentials securely before using affected features. The original credentials have not been revoked. A shared JWT signing key embedded in client apps should not be treated as confidential; review the authentication design before deployment.

Automated checks are not a guarantee that all sensitive information has been removed.
