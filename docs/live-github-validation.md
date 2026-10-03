# Live GitHub validation and follow-up build

Date: 2026-09-30. **70 PASS, 6 BLOCKED, 0 NOT COMPLETE** required sections. Overall v1 release acceptance remains **BLOCKED**. This record supersedes the earlier test counts and artifact hashes in `verification.md`.

## Live results

Used the private development GitHub App `jourfold-dev` on the maintainer's account. The owner completed device authorization. The reusable GitHub provider and Jourfold credential broker used the App user token; the GitHub CLI login was used only for independent read-only confirmations.

| Check | Result |
| --- | --- |
| Device authorization | PASS; actual BeginAsync/CompleteAsync, user browser approval |
| Native Linux secret persistence | PASS; GNOME Secret Service, fresh store instance reads; no plaintext fallback used |
| HTTPS transport | PASS; SDK push, fetch, clone and validated reopen |
| SSH transport | PASS; existing SSH configuration/credentials, strict host-key verification, SDK clone/version/push/fetch |
| Cross-transport sync | PASS; HTTPS clone safely fast-forwarded to SSH-created commit `5f125b34fbd31e44b31756b0de2038de1cc9017c` |
| Discovery | PASS; granted-installation enumeration and valid `travel.yaml` recognition |
| Client discovery | PASS; actual Jourfold command cloned and opened selected repository, with scripted dialog choices |
| Private publishing | PASS; SDK and actual Jourfold Publish command created private repositories, configured origin and pushed initial versions |
| Privacy warning | PASS; live public/private metadata and actual ordinary-open warning using an additional public `git/git` remote for metadata only; that local remote was removed afterward |
| Credential exposure checks | PASS within inspected scope; token and Basic authorization encoding absent from canonical YAML, local Git configs and captured validation logs |
| Collaborator invitation | PENDING; user chose to proceed without a collaborator for initial testing; no invitation sent |

The dialog choices in client-command probes are scripted test inputs. These runs do not claim that native GitHub dialogs, file pickers or assistive technology were fully audited.

## Client fix discovered during validation

Publishing succeeded, but the library retained its local-only label. Remote addition and publishing now share a state-update method that immediately records the remote-backed library entry, preferred remote and sharing action, and refreshes privacy information. This happens after the remote is configured, even if the initial push fails, so a retry does not present the trip as unpublished.

Two regression cases cover successful and failed initial pushes with real temporary Git repositories and a mocked HTTP creation boundary. A subsequent live client remote-add/sync check confirmed the library update against the private published repository.

## Test repositories retained for review

All contain synthetic validation data and remain private:

- `OWNER/jourfold-smoke`: owner-created fixture; authentication/discovery and HTTPS/SSH synchronization.
- `OWNER/jourfold-publish-smoke-20260930`: SDK private creation and initial push.
- `OWNER/jourfold-client-smoke-20260930`: Jourfold Publish command and library-state validation.

No source-code repository was uploaded, no public repository was written, and no collaborator was invited. The test repositories are retained rather than deleted.

## Clean verification

After the fix, removed source/test/sample bin/obj in both repositories and ran locked restore, Release builds, formatting verification, full test suites, notices and dependency audits. **TravelRepo 35/35 and Jourfold 31/31 passed**, with no skipped tests. Builds had zero warnings/errors. Both desktop packages were regenerated; source lock-file hashes stayed unchanged, and both solutions passed locked restore after packaging.

Workspace driver: `.tools/live_final_verify.py`. Evidence is copied to `artifacts/verification/2026-09-30/`. Live probes and their source are retained under `.tools/github-live-smoke/` and `.tools/github-client-smoke/`. Logs contain results and repository identifiers, not credentials. Desktop artifact contents were checked for fonts, notices, licenses, example plugin and Windows MinGit.

## First local trial

From the bootstrap workspace, run:

```sh
./.tools/start-jourfold-validation.sh
```

Run this from a terminal in the Ubuntu GNOME session. It launches the refreshed self-contained Linux build with separate validation settings, the public App client ID, the synthetic trip in Recent trips and the locally extracted Secret Service helper. It uses the credential already stored in GNOME Keyring. Authorization may need to be repeated after the App token expires.

A collaborator is unnecessary for creating/editing trips, versions, variants, synchronization, discovery, private publishing and export. Invitation testing can follow later. CachyOS/GNOME has not yet been exercised; use the portable Linux archive there with Git and a working Secret Service helper.

## Remaining gates

Windows execution/MinGit/installer checks, complete native accessibility validation, Windows Credential Manager, collaborator invitation, and hosted CI remain pending. Source-repository creation/push requires the pending publication approval. No later native Windows or hosted CI success is claimed.

## Refreshed desktop artifacts

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `jourfold-linux-x64.tar.gz` | 83625407 | `db7a1ba44b4cc8a78482be2878b2dee50014be9c03ce65d1fffeb749cb4653bd` |
| `jourfold_0.1.0_amd64.deb` | 65166278 | `2036bb8663a0bab3d460757107ead0a771e0ceaed9989b9fc01e8da247493a28` |
| `jourfold-win-x64.zip` | 127544477 | `56d688b9647b12bb0888ec46d23d0062029e9df647c08b0305bd07b788638dca` |
