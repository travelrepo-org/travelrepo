# AI assistants (MCP) and the agent guide

TravelRepo can serve one trip to an AI assistant through the [Model Context Protocol](https://modelcontextprotocol.io/) (MCP). The assistant starts the server as a local process and talks to it over standard input and output. Nothing is sent over the network by the server, and it has no AI logic of its own: it only offers the trip's data and the TravelRepo commands to change it.

There is also an agent guide, [`skills/travelrepo/SKILL.md`](../skills/travelrepo/SKILL.md). It explains the file format and the editing rules to assistants. The MCP server hands it out through its `read_guide` tool, and assistants that work on files directly can use it as a skill or as project instructions.

## Starting the server

```sh
travelrepo mcp /path/to/trip               # read and write
travelrepo mcp /path/to/trip --read-only   # only the tools that read
```

Jourfold users do not need the TravelRepo command line. The Jourfold application starts the same server with `--mcp /path/to/trip`, and Jourfold shows a ready-made configuration for the open trip (see the Jourfold user guide).

Most MCP clients (Claude Desktop, LM Studio, Cursor and others) use this configuration format:

```json
{
  "mcpServers": {
    "kyoto-trip": {
      "command": "travelrepo",
      "args": ["mcp", "/home/alex/Trips/Kyoto"]
    }
  }
}
```

Each server serves exactly one trip folder. Add one entry per trip you want the assistant to work on.

## Tools

| Tool | Changes the trip | Purpose |
|---|---|---|
| `read_guide` | no | The agent guide: entity types, fields, time format, editing rules. |
| `get_trip` | no | Title, dates, timezone, people, variants, entity counts, problems, unsaved changes. |
| `list_entities` | no | Entities with id, title and, for schedule items, time and place; filter by type or text. |
| `get_entity` | no | All fields of one entity, its note texts, its parent and what refers to it. |
| `get_schedule` | no | The schedule day by day in the trip timezone, then the unscheduled ideas. |
| `get_schema` | no | The JSON Schema of an entity type. |
| `validate` | no | Errors and warnings for the whole trip. |
| `list_versions` | no | Saved versions (Git commits), newest first. |
| `compare` | no | Entities that differ between a version or variant and the current files. |
| `create_entity` | yes | New schedule item, place, person, booking, task, expense, budget, collection or note. |
| `update_entity` | yes | JSON merge patch on any entity, including the trip manifest. |
| `delete_entity` | yes | Deletes an entity and its notes; refuses while other entities still refer to it. |
| `add_note` | yes | Adds a Markdown note to an entity. |
| `create_version` | yes | Saves the current changes as a version. Assistants are told to do this only when asked. |

Results are compact JSON. Times are shown as readable local times, for example `2027-05-14 Fri 09:00 to 11:00 Asia/Tokyo`.

## Safety

- Every change goes through `TravelRepository.ApplyAsync`, the same validated transaction that Jourfold uses. Invalid data is refused with the failed rules, and nothing is written.
- Each call reads the current files. If Jourfold or another program wrote in between, the change is planned again on the new files instead of overwriting them.
- The server reaches only the trip folder it was started with. It cannot switch variants, merge, push or touch remotes.
- With `--read-only`, the writing tools are not offered at all.
- Jourfold notices the assistant's changes within a few seconds and shows them as changes that are not yet in a version. The person decides whether to keep them as a version.

## Without MCP

An assistant that can edit files and run commands can work on the trip folder directly. Give it the agent guide, let it edit the YAML files and have it run `travelrepo validate` after each series of changes. This path has no transaction protection, so prefer the MCP server when the trip is open in Jourfold at the same time.
