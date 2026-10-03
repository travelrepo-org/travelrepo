# Plugin API 1

`TravelRepo.Plugins.Abstractions` is Apache-2.0 and independent of Avalonia and Jourfold UI types.

Implement `ITravelPlugin`. `Describe()` returns a stable ID, API version, component definitions, declarative editor fields and commands. `InvokeAsync` accepts/returns JSON-compatible data and supports cancellation. Custom types and component keys use reverse-domain namespaces.

A package directory contains:

```text
jourfold.plugin.json
Plugin.dll
other managed dependencies
LICENSES/
```

The manifest includes `id`, `version`, `minimumApiVersion`, `entryAssembly`, `capabilities` and declared `permissions`. See `jourfold/samples/Jourfold.ExamplePlugin` for an executable example.

The host exchanges JSON-RPC 2.0 on stdin/stdout, one JSON object per line. `describe` queries metadata; other methods invoke commands. Requests have integer IDs, `method` and `params`; responses contain the same ID and `result` or `error`. Plugins must not write logs to stdout. Use stderr for diagnostics without secrets.

Jourfold starts one host process per plugin and loads it into an AssemblyLoadContext. The application renders declared fields using its own controls. The v1 host is a crash/dependency boundary, **not a security sandbox**. Only run trusted installed software. Permission declarations are informational and consent-oriented.

Missing plugins do not affect retention of unknown compatible data.
