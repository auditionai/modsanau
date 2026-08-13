# AUDITION AI MOD STUDIO — SECURITY CHECKLIST V2

## Client
- [ ] Release binary signed + timestamped.
- [ ] Installer signed.
- [ ] Update manifest/package signed and hash verified.
- [ ] No AI provider secret.
- [ ] No Supabase service-role key.
- [ ] No payment webhook secret.
- [ ] No template master encryption key.
- [ ] Tokens stored using Windows secure storage abstraction.
- [ ] Obfuscation applied only to Release after compatibility testing.
- [ ] Native AOT/native modules evaluated, not treated as DRM boundary.
- [ ] ACV Tool 5 executable hash verified before launch.
- [ ] Absolute paths used for tools/native libraries.
- [ ] No shell command concatenation from untrusted input.
- [ ] Path traversal blocked.
- [ ] Temp workspaces randomized and cleaned.
- [ ] Sensitive values redacted from logs/crash dumps.

## ACV Tool 5 / Keydat
- [ ] Never automate country selection with SendKeys/mouse/UI Automation.
- [ ] `acv.exe` is launched with absolute path and redirected stdin/stdout/stderr.
- [ ] AuditionVN selection (`1`) comes from a trusted `GameRegionProfile`, not user-controlled command text.
- [ ] `Select:` prompt without newline cannot deadlock the runner.
- [ ] Missing keydat and existing keydat flows are both tested.
- [ ] Generated `.keydat` is scoped to isolated working/project workspace.
- [ ] Keydat is not treated as an encryption key, license secret or DRM boundary.
- [ ] Concurrent projects do not share one writable keydat.
- [ ] Extract success validates folder/files and `writing :` progress where applicable.
- [ ] Pack success validates output archive and `Packing:` progress where applicable.

## Templates / Archives
- [ ] Template/archive input chỉ đến từ explicit user-selected file hoặc authorized cloud distribution; không discover hay derive từ Audition installation.
- [ ] Raw premium templates not openly bundled in installer.
- [ ] Template catalog server-authorized.
- [ ] Short-lived download authorization.
- [ ] Template package authenticated/integrity checked.
- [ ] Encrypted local cache where useful.
- [ ] Global pristine template never modified.
- [ ] Project operates on working copy.
- [ ] Template version + SHA-256 stored.
- [ ] Exposure limitations documented honestly.

## Backend
- [ ] RLS enabled and tested.
- [ ] Wallet mutations server-only.
- [ ] Credit reservation transactional.
- [ ] Idempotency for AI/payment requests.
- [ ] Rate limits.
- [ ] Ownership checks.
- [ ] Upload size/MIME validation.
- [ ] Signed URLs expire.
- [ ] Provider errors refund reserved credits correctly.
- [ ] Payment credits only after verified webhook.

## Release / Supply Chain
- [x] Secret scanning in CI.
- [x] Dependency vulnerability scan.
- [x] SBOM generated and verified against the locked NuGet graph.
- [ ] Signing key not in repo/runner disk as plaintext.
- [x] Third-party license inventory reviewed; `NOASSERTION` entries remain a commercial release blocker.
- [x] Redistribution rights reviewed; ACV Tool/templates/game assets remain blocked pending written approval.

## Tests
- [ ] First extract without `015.keydat` automatically provides AuditionVN selection `1`.
- [ ] Extract with existing `015.keydat` does not wait forever for `Select:`.
- [ ] Pack with missing keydat can regenerate it using selection `1`.
- [ ] `Select:` without newline is handled.
- [ ] Paths with spaces/Vietnamese Unicode work.
- [ ] Modified acv.exe rejected.
- [ ] Modified template package rejected.
- [ ] Wrong user data access rejected.
- [ ] Forged local credit ignored.
- [ ] Replay/duplicate AI charge safe.
- [ ] Concurrent spend safe.
- [ ] Malformed DDS handled without crash/corruption.
- [ ] Path traversal rejected.
- [ ] Update with wrong signature rejected.
- [ ] Search release files for secrets returns none.
