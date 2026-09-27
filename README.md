# LayoutSync

LayoutSync keeps a `layouts/` directory of JSON files (layouts, sections, menus, manifests, seed entities, and so on) in sync with RavenDB, and edits each layout's `layout-manifest.json` (its route configuration and its section registry) without hand-editing the file.

It ships two programs:

- **`layoutsync`**: a command-line tool that syncs files to the database, validates them, and edits manifests.
- **`layoutsync-mcp`**: an [MCP](https://modelcontextprotocol.io) server that exposes the manifest read and edit operations as tools for AI agents such as Claude Code.

## Projects

| Project | What it is |
|---|---|
| `LayoutSync.Core` | Class library: sync, validation, and manifest-mutation services shared by both programs. |
| `LayoutSync` | The `layoutsync` CLI (System.CommandLine). |
| `LayoutSync.Mcp` | The `layoutsync-mcp` MCP server (stdio transport). |
| `LayoutSync.Tests` | xUnit test suite. |

## Build, test, publish

Requires the .NET 10 SDK.

```bash
dotnet test LayoutSync.sln
```

For local use, publish both programs into `release/` (gitignored) and put that directory on your `PATH`:

```bash
dotnet publish LayoutSync/LayoutSync.csproj -c Release -o release --self-contained false
dotnet publish LayoutSync.Mcp/LayoutSync.Mcp.csproj -c Release -o release --self-contained false
```

`dotnet publish` does not create the short command names, so add two one-line wrappers to `release/` once. On Windows: `layoutsync.cmd` containing `@"%~dp0LayoutSync.exe" %*`, and `layoutsync-mcp.cmd` containing `@"%~dp0LayoutSync.Mcp.exe" %*`. On macOS or Linux, use symlinks or equivalent shell scripts. The `.mcp.json` example below runs the server by the name `layoutsync-mcp`. A running MCP server keeps using the binary it started with, so restart your MCP client after republishing.

Pushing a `v*` tag publishes a framework-dependent CLI tarball as a GitHub Release, for CI consumers (see `.github/workflows/release.yml`).

## CLI

Run `layoutsync --help` (and `layoutsync manifest --help`, then `--help` on any manifest subcommand) for the full option list. Common invocations:

```bash
layoutsync --sync-once --preserve-ids        # sync every layout once, then exit
layoutsync --layout dirt-life                # sync one layout, then keep watching and re-sync on every change
layoutsync manifest set-route /events --layout dirt-life --structural-section full-width-layout --dry-run
layoutsync manifest rename-section full-width-layout dirt-life-full-width-layout --layout dirt-life --dry-run
```

In Git Bash on Windows, a route argument such as `/events` is rewritten into a filesystem path before LayoutSync sees it. Write it as `//events`, set `MSYS_NO_PATHCONV=1`, or use PowerShell.

When `--layouts-path` is omitted, the CLI looks for a `layouts/` directory in the current directory and then in each parent directory, so running it from inside a checkout targets that checkout. `--json` on the `manifest` subcommands prints the same envelope the MCP mutation tools return (see [Responses](#responses)).

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Error: bad configuration or input, an I/O failure, or a rejected manifest change. |
| `2` | `--strict` was set and a validator reported a problem. For the section-registry subcommands, any warning counts. |
| `3` | Refused to write to a non-local RavenDB server. Pass `--allow-remote-sync` if you mean it. |
| `4` | Refused because the current directory is inside a worktree (`.claude/worktrees/<name>/`) but the layouts path points outside it. Pass `--allow-cross-worktree-sync` if you mean it. |

### Section registry

`entities.sections` lists the sections a layout declares, and `set-route` accepts only identifiers declared there. Three subcommands change the list. They take the same options as `set-route` (`--layout`, `--layouts-path`, `--dry-run`, `--json`, `--strict`, `--allow-cross-worktree-sync`):

```bash
layoutsync manifest add-section events-page-header --layout dirt-life --description "Events header"
layoutsync manifest rename-section full-width-layout cream-pi-full-width-layout --layout cream-pi --dry-run
layoutsync manifest remove-section old-hero --layout dirt-life
```

- **`add-section`** appends an entry. `--type` defaults to `ui-schema-section`, and `--file` defaults to `sections/<identifier>.json`, relative to the layout directory. It refuses an identifier that is already declared. It warns, but still adds the entry, when the section file is missing or declares a different `identifier`.
- **`rename-section`** changes three things in one step:
  - the entry's `identifier`;
  - every field in the manifest that references the section, all in a single write;
  - the section file's own top-level `identifier`, which is the name the sync gives the document.

  Only that one value in the section file changes. The rest of the file stays byte-for-byte, and the file keeps its name. `--manifest-only` leaves the section file alone and reports the mismatch as a warning.
- **`remove-section`** refuses while any field still references the section, and lists those fields. `--force` removes the entry anyway and reports each reference it leaves dangling as a warning. The section file is not deleted, so the sync keeps syncing it until you delete it.

A rename changes both files or neither:

- If the section file can't be updated (unreadable, not valid JSON, or declaring a different identifier), the rename refuses before writing anything. Fix the file, or use `--manifest-only`.
- A missing section file doesn't block the rename, because there is nothing to update. It is reported as a warning.
- If writing the section file fails, the manifest is put back.
- If even putting the manifest back fails, the command exits `1`. The error names the file that changed and says how to finish or undo the rename.

**What counts as a reference:** any string value outside `entities.sections` whose field name contains `section` and whose value is the identifier. That covers every field the layouts use today:

| Field | Used for |
|---|---|
| `routeConfigs.<route>.structuralSection` | a route's page structure |
| `routeConfigs.<route>.patches[].sectionIdentifiers[]` | the sections filling a route's slots |
| `routing.notFoundSection` | the section a 404 renders |
| `onboarding.structuralSection`, `onboarding.formSection` | onboarding |
| `authoring.targetSection`, `authoring.editableSections[]` | in-app authoring |

A new field that follows the same naming convention is covered automatically. If the identifier turns up in a field that doesn't follow it, rename and remove leave that value alone and warn, naming where it is.

References are reported as [JSON Pointers](https://www.rfc-editor.org/rfc/rfc6901), with `/` inside a route key written as `~1`. For example, `/routeConfigs/~1events~1list/structuralSection` is the `structuralSection` of the `/events/list` route.

## MCP server

### Configuration

Register the server in your MCP client. For Claude Code, add it to the project's `.mcp.json`:

```json
{
  "mcpServers": {
    "layoutsync": {
      "command": "layoutsync-mcp",
      "env": { "LAYOUTSYNC_LAYOUTS_PATH": "layouts" }
    }
  }
}
```

`LAYOUTSYNC_LAYOUTS_PATH` is required and sets the server's **default** `layouts/` directory. A relative value is resolved against the directory the client starts the server in; for Claude Code, that is the directory the session started in. The server refuses to start if the directory does not exist.

### Tools

| Tool | Reads or writes | Use it to |
|---|---|---|
| `manifest_list_sections` | reads | List the valid section identifiers (pick from these to avoid typos). |
| `manifest_list_routes` | reads | List every route with its structural section and filled slots. |
| `manifest_get_route` | reads | Get one route's full configuration. |
| `manifest_set_route` | writes | Create or change one route. Supports `dryRun`. |
| `manifest_apply_batch` | writes | Apply several route patches, either all-or-nothing (`onError: "abort"`) or skipping invalid ones (`onError: "skip"`). Supports `dryRun`. |
| `manifest_add_section` | writes | Declare a section in `entities.sections` (same as `add-section`). Supports `dryRun`. |
| `manifest_rename_section` | writes | Rename a section everywhere in the manifest, and in its section file unless `manifestOnly: true` (same as `rename-section`). Supports `dryRun`. |
| `manifest_remove_section` | writes | Remove a section. Refuses while it is referenced, unless `force: true` (same as `remove-section`). Supports `dryRun`. |

Every tool takes a `layoutId` and an optional `layoutsPath` (next section). Run a write with `dryRun: true` first: it returns the full before/after diff without touching the file.

### Working in more than one git worktree

The server chooses its default `layouts/` directory once, when it starts. If your session then edits a **different** git worktree (for example, it started in one checkout and created a new worktree for a fix), the default still points at the first checkout. Without further information the tools would read that checkout's manifests and write your changes into it, while the worktree you are actually editing never gets them.

The server cannot see which checkout you are editing, so the tools handle this in four ways:

1. **`layoutsPath` names the checkout.** Every tool accepts an optional `layoutsPath`. Pass it on every call whenever you edit a checkout other than the one the server was started in. It accepts either the checkout's `layouts/` directory or the checkout (worktree) root that contains `layouts/`. It must be an absolute path (on Windows, include the drive letter). A relative path is rejected, because it would be resolved against the server's start directory, which is the very default you are trying to override. The path is used exactly as given: the server does not search parent directories. Worktrees often live inside the primary checkout (`<primary>/.claude/worktrees/<name>`), so searching upward from a worktree with no `layouts/` of its own would silently land on the primary checkout's `layouts/`.
2. **Every response shows its target.** Each response includes `manifestPath`, the exact file that was read or written. Error messages include the path too.
3. **Writes without `layoutsPath` are flagged.** The write tools still run without `layoutsPath`, but they add a warning to `warnings` that names the file they used. If you dry-run first, you see that warning before anything is written. The tools warn instead of refusing because a refusal would also block the normal case, a session editing the checkout it started in, and would make `layoutsPath` required in practice.
4. **The default is announced up front.** During the MCP handshake the server sends instructions that state its default `layouts/` path. Claude Code shows these to the model, so an agent can compare the default with the checkout it is editing before its first call.

Example: a dry-run edit aimed at a worktree:

```json
{
  "layoutId": "dirt-life",
  "route": "/events/my-rsvps",
  "mainSections": ["my-rsvps-list"],
  "dryRun": true,
  "layoutsPath": "/src/app/.claude/worktrees/my-fix"
}
```

### Responses

The write tools return the same JSON envelope as the CLI's `--json` output:

```json
{
  "command": "manifest set-route",
  "layoutId": "dirt-life",
  "manifestPath": "/src/app/.claude/worktrees/my-fix/layouts/dirt-life/manifests/layout-manifest.json",
  "dryRun": true,
  "success": true,
  "changes": [
    {
      "route": "/events/my-rsvps",
      "status": "applied",
      "before": { "structuralSection": "sidebar-layout", "patches": [] },
      "after": {
        "structuralSection": "sidebar-layout",
        "patches": [ { "targetElementId": "main", "sectionIdentifiers": ["my-rsvps-list"] } ]
      },
      "patch": [
        { "op": "replace", "path": "/patches", "value": [ { "targetElementId": "main", "sectionIdentifiers": ["my-rsvps-list"] } ] }
      ],
      "error": null
    }
  ],
  "warnings": [],
  "errors": []
}
```

- `changes[].status` is `applied`, `skipped` (invalid, with `onError: "skip"`), or `aborted` (the batch was rejected).
- `before` and `after` are the route's configuration before and after the change, and `patch` is an RFC 6902 JSON Patch between them.

The section tools, and the section subcommands with `--json`, return the same frame with section-specific fields:

```json
{
  "command": "manifest rename-section",
  "layoutId": "cream-pi",
  "manifestPath": "/src/app/layouts/cream-pi/manifests/layout-manifest.json",
  "dryRun": true,
  "success": true,
  "identifier": "full-width-layout",
  "newIdentifier": "cream-pi-full-width-layout",
  "before": { "identifier": "full-width-layout", "type": "ui-schema-section", "file": "sections/full-width-layout.json" },
  "after": { "identifier": "cream-pi-full-width-layout", "type": "ui-schema-section", "file": "sections/full-width-layout.json" },
  "patch": [
    { "op": "replace", "path": "/entities/sections/0/identifier", "value": "cream-pi-full-width-layout" },
    { "op": "replace", "path": "/routeConfigs/~1/structuralSection", "value": "cream-pi-full-width-layout" }
  ],
  "references": ["/routeConfigs/~1/structuralSection"],
  "filesChanged": [
    "/src/app/layouts/cream-pi/manifests/layout-manifest.json",
    "/src/app/layouts/cream-pi/sections/full-width-layout.json"
  ],
  "warnings": [],
  "errors": []
}
```

- `before` and `after` are the `entities.sections` entry, and are `null` on the side where it does not exist.
- `patch` is an RFC 6902 JSON Patch against the whole manifest. It is `null` when the change was refused and nothing was written.
- `references` lists the fields that named the section. A rename rewrote them; a remove is blocked by them, or leaves them dangling with `force`; an add resolves them.
- `filesChanged` lists every file written, including a rewritten section file. With `dryRun`, it lists the files that would be written.

The read tools return `layoutId`, `manifestPath`, and their own payload (`routes`, `sections`, or `route` / `found` / `config`). When a manifest cannot be found or parsed, a read tool returns an MCP tool error whose message names the path it tried.
