# TravelRepo

An open Git-native trip format and .NET 10 SDK, licensed under Apache-2.0. Jourfold is a separate desktop client.

TravelRepo includes lossless YAML serialization, JSON Schema 2020-12 artifacts, semantic validation, recoverable repository writes, real Git versions and variants, semantic diff/merge, GitHub provider capabilities, plugin contracts, exports and a CLI.

```sh
dotnet build TravelRepo.sln
dotnet test TravelRepo.sln
dotnet run --project src/TravelRepo.Cli -- --help
```

Use .NET SDK 10.0.401 and Git. See [development](docs/development.md), [schemas](docs/schemas.md), [CLI](docs/cli.md), [providers](docs/providers.md), [plugins](docs/plugins.md), and the [specification](docs/TRAVELREPO_SPEC.md). Samples are validated by the conformance suite.

This implementation is under v1 acceptance validation. See [acceptance status](docs/acceptance.md) for verified behavior and remaining gaps.
