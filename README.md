# LayoutSync

LayoutSync keeps a `layouts/` directory of JSON files (layouts, sections, menus, manifests, seed entities, and so on) in sync with RavenDB, and edits the route configuration in each layout's `layout-manifest.json` without hand-editing the file.

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

Run `layoutsync --help` (and `layoutsync manifest set-route --help`, `layoutsync manifest from-json --help`) for the full option list. Common invocations:

```bash
layoutsync --sync-once --preserve-ids        # sync every layout once, then exit
layoutsync --layout dirt-life                # sync one layout, then keep watching and re-sync on every change
layoutsync manifest set-route /events --layout dirt-life --structural-section full-width-layout --dry-run
```

In Git Bash on Windows, a route argument such as `/events` is rewritten into a filesystem path before LayoutSync sees it. Write it as `//events`, set `MSYS_NO_PATHCONV=1`, or use PowerShell.

When `--layouts-path` is omitted, the CLI looks for a `layouts/` directory in the current directory and then in each parent directory, so running it from inside a checkout targets that checkout. `--json` on the `manifest` subcommands prints the same envelope the MCP mutation tools return (see [Responses](#responses)).

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Error: bad configuration or input, an I/O failure, or a rejected manifest change. |
| `2` | `--strict` was set and a validator reported a problem. |
| `3` | Refused to write to a non-local RavenDB server. Pass `--allow-remote-sync` if you mean it. |
| `4` | Refused because the current directory is inside a worktree (`.claude/worktrees/<name>/`) but the layouts path points outside it. Pass `--allow-cross-worktree-sync` if you mean it. |

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

Every tool takes a `layoutId` and an optional `layoutsPath` (next section). Run a write with `dryRun: true` first: it returns the full before/after diff without touching the file.

### Working in more than one git worktree

The server chooses its default `layouts/` directory once, when it starts. If your session then edits a **different** git worktree (for example, it started in one checkout and created a new worktree for a fix), the default still points at the first checkout. Without further information the tools would read that checkout's manifests and write your changes into it, while the worktree you are actually editing never gets them.

The server cannot see which checkout you are editing, so the tools handle this in four ways:

1. **`layoutsPath` names the checkout.** Every tool accepts an optional `layoutsPath`. Pass it on every call whenever you edit a checkout other than the one the server was started in. It accepts either the checkout's `layouts/` directory or the checkout (worktree) root that contains `layouts/`. It must be an absolute path (on Windows, include the drive letter). A relative path is rejected, because it would be resolved against the server's start directory, which is the very default you are trying to override. The path is used exactly as given: the server does not search parent directories. Worktrees often live inside the primary checkout (`<primary>/.claude/worktrees/<name>`), so searching upward from a worktree with no `layouts/` of its own would silently land on the primary checkout's `layouts/`.
2. **Every response shows its target.** Each response includes `manifestPath`, the exact file that was read or written. Error messages include the path too.
3. **Writes without `layoutsPath` are flagged.** `manifest_set_route` and `manifest_apply_batch` still run without `layoutsPath`, but they add a warning to `warnings` that names the file they used. If you dry-run first, you see that warning before anything is written. The tools warn instead of refusing because a refusal would also block the normal case, a session editing the checkout it started in, and would make `layoutsPath` required in practice.
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

The read tools return `layoutId`, `manifestPath`, and their own payload (`routes`, `sections`, or `route` / `found` / `config`). When a manifest cannot be found or parsed, a read tool returns an MCP tool error whose message names the path it tried.
