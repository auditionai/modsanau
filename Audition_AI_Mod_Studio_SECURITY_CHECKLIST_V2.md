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
- [x] Internal manual release penetration/crack-resistance review covers the exact eight PLAN 90 attack paths.
- [x] Public Release artifact excludes PDB/source/private maps/keys and proprietary fixtures before signing.
- [x] Runtime manifest is `asInvoker`; real file-only tools were tested from a medium-integrity standard-user token.
- [ ] Independent commissioned penetration test/security-owner production sign-off completed if required by risk policy.

## Tests
- [ ] First extract without `015.keydat` automatically provides AuditionVN selection `1`.
- [ ] Extract with existing `015.keydat` does not wait forever for `Select:`.
- [ ] Pack with missing keydat can regenerate it using selection `1`.
- [ ] `Select:` without newline is handled.
- [x] Paths with spaces/Vietnamese Unicode work (PLAN 97 real extract/build/export/re-extract gate).
- [x] Modified acv.exe rejected.
- [x] Corrupt template/archive source rejected.
- [x] Modified template package rejected.
- [x] Wrong user data access rejected.
- [x] Forged local credit ignored.
- [x] Expired token rejected.
- [x] Replay/duplicate AI charge safe.
- [x] Concurrent spend safe.
- [x] Malformed DDS/archive handled without crash/corruption or workspace escape.
- [x] Path traversal rejected.
- [x] Update with wrong signature rejected.
- [x] Search source and Release files for secrets returns none.

PLAN 89 ánh xạ 12/12 mục trên tới test thực thi tại `docs/SECURITY_TEST_MATRIX.md`. PostgreSQL local và ACV fixture thật
được dùng cho các boundary tương ứng; đây không phải bằng chứng Supabase staging/live hay production signing/CDN.
