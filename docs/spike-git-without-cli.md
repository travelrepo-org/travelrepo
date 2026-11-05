# Spike: Git without the git program

Android and iOS have no `git` program, and `GitCliBackend` runs one. This spike checked whether TravelRepo can do
everything it does with Git through libgit2 instead, on Android, including sync with a host.

Answer: yes. The checks below passed on an Android 15 emulator and on a Pixel 2 with Android 11.

## What was built

- **`TravelRepo.Git.LibGit2`**, a separate package so `TravelRepo.Git` keeps no native dependency. `LibGit2Backend`
  implements `IGitBackend` with LibGit2Sharp. It understands the commands `GitRepository` sends (about 30 forms) and
  answers in Git's output format and with Git's wording, so `GitErrorText.Classify` sorts failures unchanged.
- **`ManagedHttpTransport`**: Git's smart HTTP protocol over `HttpClient`, registered for `http` and `https`.
  libgit2 needs no TLS library; Android uses its own TLS and certificates. Sign-in comes from `ICredentialBroker`.
- **`eng/build-libgit2-android.sh`** builds libgit2 1.8.6 (the commit LibGit2Sharp 0.32 loads) for `arm64-v8a` and
  `x86_64`: 1.6 MB each after stripping, linked only against libc, libm and libdl, aligned for 16 KB pages.
  Rebuilding gives the same files apart from the build ID.
- `TRAVELREPO_TEST_GIT=libgit2` runs the test suite through the new backend.

## Results

| Check | Result |
|---|---|
| TravelRepo tests with `git` / with libgit2 | 190 / 190 pass with both (3 s with libgit2, 4 to 5 s with `git`) |
| Jourfold tests with libgit2 (local trial, not committed) | 133 / 133 pass |
| Android: new trip, 20 versions, variant, switch, history, snapshot, overview | works on both devices |
| Android: push with sign-in, clone, sync both ways, diverged state | works on both devices |
| Android: wrong password, unreachable host | reported as `git.auth` and `git.network` |
| Android: HTTPS clone from github.com | works |

Times on the Pixel 2:

| Operation | Time |
|---|---|
| First version of a new trip | 0.46 s |
| One more version | about 0.47 s |
| Create a variant | 1.0 s |
| Switch variant | 0.09 s |
| History of 21 versions | 0.08 s |
| Push with sign-in / sync up / sync down (local network) | 0.31 / 0.36 / 0.20 s |

## Limits and open points

- **Saving a version is slow on old phones.** `CreateVersionAsync` sends a dozen commands, and each opens the
  repository again and scans the folder. A method that stages all trip paths in one pass would cut most of it.
- **The command line is emulated.** This kept the test suite unchanged, which made the comparison reliable. Commands
  outside the known forms fail with exit code 129. Moving `IGitBackend` to typed operations would remove the parsing,
  but it is a larger change to `TravelRepo.Git`.
- **No SSH**, and the user's Git credential helpers and `~/.ssh` are not used. Sign-in works through
  `ICredentialBroker` only (GitHub tokens and similar).
- **Licence:** libgit2 is GPL-2.0 with a linking exception, LibGit2Sharp is MIT. A separate Apache-2.0 package that
  loads libgit2 is allowed; the libgit2 licence and a pointer to its source belong in the third-party notices of
  anything that ships the library.
- The Android binaries come from LibGit2Sharp's version of libgit2. When LibGit2Sharp updates, the build has to follow,
  because the library name contains the commit.
- **iOS:** `eng/build-libgit2-ios.sh` builds the same commit as `git2.xcframework` (devices and simulator) on a Mac.
  iOS loads libraries only from frameworks, so the app calls `LibGit2Backend.UseNativeLibrary` with the path of
  `git2.framework/git2` before the first Git command. LibGit2Sharp passes delegates to libgit2 (the HTTP transport,
  progress, sign-in) without `MonoPInvokeCallback`, so a device build needs the Mono interpreter enabled
  (`UseInterpreter`), which turns such delegates into native callbacks that ahead-of-time code alone cannot.
- An Android app must exclude the native files of `LibGit2Sharp.NativeBinaries`
  (`ExcludeAssets="native;build;buildTransitive"`). Otherwise Android picks the glibc build for Linux through the RID
  fallback, and it cannot load.
