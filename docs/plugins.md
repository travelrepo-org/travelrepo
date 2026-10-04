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

The manifest requires `id`, `version`, `minimumApiVersion` and `entryAssembly`, and lists `capabilities` and declared `permissions`. These optional members describe the plugin to people, for example in an About window:

| Member | Meaning |
| --- | --- |
| `name` | Display name. Hosts show the ID when it is missing. |
| `description` | One or two sentences about what the plugin adds. |
| `authors` | Author or organisation names. |
| `license` | SPDX license expression, such as `MIT` or `Apache-2.0`. |
| `homepage` | An `https://` address with documentation or source code. Other schemes are ignored. |
| `licenseFile` | Path of the full license text inside the package. Without it, hosts use the only file in `LICENSES/`. Paths outside the package are ignored. |

```json
{
  "id": "org.travelrepo.walking",
  "version": "1.0.0",
  "minimumApiVersion": 1,
  "entryAssembly": "Jourfold.ExamplePlugin.dll",
  "capabilities": ["components", "commands"],
  "permissions": [],
  "name": "Walking routes",
  "description": "Adds a walking component with distance and pace.",
  "authors": "Jourfold contributors",
  "license": "GPL-3.0-or-later",
  "homepage": "https://example.org/walking",
  "licenseFile": "LICENSES/GPL-3.0-or-later.txt"
}
```

`PluginManifest.Parse` reads a manifest and checks the required members. `DisplayName`, `HomepageUri` and `ResolveLicenseFile` apply the fallbacks above, so hosts do not need their own rules. See `jourfold/samples/Jourfold.ExamplePlugin` for an executable example.

The host exchanges JSON-RPC 2.0 on stdin/stdout, one JSON object per line. `describe` queries metadata; other methods invoke commands. Requests have integer IDs, `method` and `params`; responses contain the same ID and `result` or `error`. Plugins must not write logs to stdout. Use stderr for diagnostics without secrets.

Jourfold starts one host process per plugin and loads it into an AssemblyLoadContext. The application renders declared fields using its own controls. The v1 host is a crash/dependency boundary, **not a security sandbox**. Only run trusted installed software. Permission declarations are informational and consent-oriented.

Missing plugins do not affect retention of unknown compatible data.
