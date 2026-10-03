# TravelRepo command line

Run from source:

```sh
dotnet run --project src/TravelRepo.Cli -- validate /path/to/trip
```

Install the packed CLI with `dotnet tool install --add-source artifacts/packages TravelRepo.Cli --tool-path /chosen/tool-directory`.

Commands:

- `init PATH TITLE`: create a local trip and initial Git version.
- `validate [PATH]`: schema and semantic diagnostics, JSON output. Exit 2 means invalid data.
- `inspect [PATH]`: title, entity counts and diagnostics.
- `diff PATH REVISION`: semantic changes from the specified Git version to the working tree.
- `repair PATH`: inspect diagnostics and show the recovery journal location.
- `repair PATH --apply`: recover interrupted transactions if their files still match recorded before/after images. Unknown corruption is reported without guessing or deleting data.
- `version PATH MESSAGE`: create a real Git version with machine-readable trailers.
- `export PATH OUTPUT.ics`: timezone-correct calendar export.
- `export PATH OUTPUT.html`: print-friendly HTML.

The default path for inspect/validate is the current directory. The CLI never changes global Git configuration. Generic remotes use normal Git SSH configuration and credential helpers.

For a malformed repository, `repair PATH --restore REVISION` previews a canonical rollback without needing to parse the broken files. Add `--apply` after review. All changed before-images are journaled outside the repository and a subsequent external modification makes the reviewed plan stale. This restores trip content as uncommitted changes; create a version after inspection.
