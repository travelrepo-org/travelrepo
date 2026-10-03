Current build: the 2026-09-30 usability revision rebuilt both repositories and packages; 37 TravelRepo and 41 Jourfold tests pass. See the current acceptance matrix and Jourfold usability review. The runs below are historical evidence.

# Verification record

Latest follow-up: [2026-09-30 live GitHub validation, client fix and refreshed packages](live-github-validation.md). Earlier results and hashes below are historical.

Date: 2026-09-29. Overall release status: **BLOCKED**; see [the complete acceptance matrix](acceptance.md).

## Build and automated verification

Host: Ubuntu 24.04 x64, .NET SDK 10.0.401, Git 2.43.0. Both repositories were built after deleting only their source/test/sample `bin` and `obj` directories. Each repository then ran:

```sh
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet format --verify-no-changes --no-restore
dotnet test -c Release --no-build --logger trx
python3 eng/notices.py
python3 eng/verify.py
```

Results: both builds passed with zero warnings/errors; formatting passed; TravelRepo **35/35** tests passed; Jourfold **29/29** passed; no skipped tests. Dependency vulnerability audits passed. Font hashes and required license/notice files passed. TravelRepo then ran `dotnet pack -c Release --no-build -o artifacts/packages`, producing nine NuGet packages, each checked for its license and notices.

An isolated copy of Jourfold with no sibling TravelRepo checkout restored from the produced package feed and built successfully. The installed `TravelRepo.Cli` tool validated the Tokyo sample with no diagnostics. This proves source-independent consumption; these packages have not been uploaded to a public feed.

Jourfold packaging ran:

```sh
python3 eng/package.py --rid linux-x64
python3 eng/package.py --rid win-x64
```

Both package operations passed. Packaging uses temporary lock files under each project's `obj` directory; it does not rewrite source lock files. SHA-256 checks before/after both package runs verified this, and both solutions subsequently passed `dotnet restore --locked-mode`. The Windows ZIP includes checksum-pinned MinGit. Linux TAR and Debian contents and Windows ZIP integrity were inspected for fonts, licenses, notices, plugin host and example plugin. The Windows installer recipe is present but has not been compiled or executed on this host.

Independent `pdftotext` reading of the exported PDF recovered the trip, activity and cross-timezone flight content. ICS includes the expected UTC flight instant. No claim is made about native Windows reader integration.

Logs and test TRX files are in this workspace's `.tools/release-verification/`; copied evidence is in each repository's ignored `artifacts/verification/`. No workspace-root Git repository exists. A source/asset/document signature scan found no GitHub token or private-key signature; this is not a substitute for the pending authenticated credential/log smoke.

## Native Linux checks

Both sessions ran on isolated X11 displays with the actual desktop shells, using the published self-contained Linux application and separate local app-state/trip directories:

| Environment | Checks performed | Result |
| --- | --- | --- |
| GNOME Shell 46.0 | Empty library without account/remote; create trip; Quick Add; canonical autosave; keyboard Undo/Redo. Final package reopened the library and trip; dark wordmark/text contrast inspected. | PASS for these checks |
| Plasma 5.27.12 / KWin 5.27.11 | Empty library; create trip; Quick Add; Undo/Redo; Create Version through its review dialog and real Git commit. Final package reopened the trip and repeated Quick Add/autosave/Undo/Redo. | PASS for these checks |

Display size was 1440×1000 at 100% with software rendering. Screenshots are under `artifacts/verification/`. KDE was extracted into an isolated local prefix, with ancillary desktop-service activation warnings and incomplete window decoration integration. These sessions establish native shell rendering and basic input, not full installed-desktop, portal/file-picker, Secret Service or assistive-technology acceptance. Those checks remain on the release checklist. Headless tests additionally cover light/dark themes, EN/DE strings, larger text/high contrast, inspector keyboard focus, actual timetable drop/move/resize, overnight/shared lanes, public-repository warnings, comparison and all four conflict actions.

## External release blockers

- Windows 11 x64 execution, clean-machine MinGit fallback, installer compilation/install and native scaling/input smoke.
- Disposable authorized HTTPS/SSH endpoint plus Windows Credential Manager and Linux Secret Service round-trips and authenticated log-redaction checks.
- GitHub App client ID, device authorization, selected installation/repository grants and a consenting test collaborator for the documented live flow.
- Full native keyboard-only and screen-reader audit on supported platforms.
- Published/configured GitHub repositories, `TRAVELREPO_REPOSITORY` Actions variable and successful Windows/Linux hosted CI runs.

No live GitHub publication, invitation, hosted CI success or Windows runtime success is claimed. There are **66 PASS, 10 BLOCKED, 0 NOT COMPLETE** required sections. A v1 release remains blocked until all ten gates pass.

## Produced desktop artifacts

Paths are relative to Jourfold's `artifacts/` directory. Checksums identify this local build and are also stored in `release-artifacts.json`.

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `jourfold-linux-x64.tar.gz` | 83625775 | `17b871a806b836de79ea7b3fd9ed0874740e8470b4226cbb8b50f140ec82ada6` |
| `jourfold_0.1.0_amd64.deb` | 65170324 | `1da121be91ccef667db7060449a40c887bb31380f16b7a6d9d54e127f4ff2280` |
| `jourfold-win-x64.zip` | 127544092 | `88e5bf7c005f6e1984fcb0df4905b3624d66b92fbe4807f43a67fff443a3dd79` |

## Follow-up: 2026-09-30

Confirmed that the GitHub CLI is authenticated as the maintainer using the OS keyring. This enables authorized repository/CI administration but does not replace Jourfold GitHub App authorization. Source repositories have no remotes yet; creating/pushing private source repositories awaits explicit publication authorization.

Found the actual Ubuntu 24.04.5 GNOME X11 session and GNOME Keyring; Orca is installed. A disposable credential was written through `Jourfold.Infrastructure.OsSecretStore`, read through a fresh instance, deleted and verified absent through another fresh instance. **PASS: native Linux Secret Service persistence.** The probe removed its own entry and did not read existing stored credentials. The required `libsecret-tools` helper was extracted from the Ubuntu package into the workspace for this run; no system package or desktop setting was changed. Log: `.tools/native-secret-probe/result.log`. Windows credential storage and authenticated transport/redaction checks remain pending.

The second intended Linux target is CachyOS with GNOME. No CachyOS run is claimed. Native keyboard/screen-reader and installed-desktop checks remain pending.
