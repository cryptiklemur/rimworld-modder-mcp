# RimWorld Modder MCP

MCP server for RimWorld mod analysis and modding workflows, implemented in C#/.NET 10.

Primary focus:

- XML defs and inheritance
- patch inspection and conflict triage
- mod compatibility and dependency checks
- Player.log triage
- release-readiness checks for RimWorld mods

## Quickstart

### Recommended: global `.NET tool`

Requires a `.NET 10 SDK`.

```bash
dotnet tool install -g cryptiklemur.rimworld-modder-mcp
```

Most MCP clients can then use:

```json
{
  "mcpServers": {
    "rimworld-modder": {
      "command": "rimworld-modder-mcp",
      "args": [
        "--rimworld-path=/absolute/path/to/RimWorld",
        "--mod-dirs=/absolute/path/to/RimWorld/Mods"
      ]
    }
  }
}
```

Repo examples:

- `claude-desktop-config.json`
- `local-mcp-config.json`

### No-install option

If you do not want a global install, use `dnx` or `dotnet tool exec`. This also requires a `.NET 10 SDK`.

Direct shell usage:

```bash
dnx cryptiklemur.rimworld-modder-mcp --yes -- --rimworld-path=/absolute/path/to/RimWorld
```

```bash
dotnet tool exec cryptiklemur.rimworld-modder-mcp --yes -- --rimworld-path=/absolute/path/to/RimWorld
```

Headless MCP config with `dnx`:

```json
{
  "mcpServers": {
    "rimworld-modder": {
      "command": "dnx",
      "args": [
        "cryptiklemur.rimworld-modder-mcp",
        "--yes",
        "--",
        "--rimworld-path=/absolute/path/to/RimWorld",
        "--mod-dirs=/absolute/path/to/RimWorld/Mods"
      ]
    }
  }
}
```

`--yes` is there so first-run package download does not pause for confirmation.

## Client Setup

Pick the client you care about:

<details>
<summary>Codex CLI</summary>

Global installed tool:

```bash
codex mcp add rimworld-modder -- \
  rimworld-modder-mcp \
  --rimworld-path=/absolute/path/to/RimWorld \
  --mod-dirs=/absolute/path/to/RimWorld/Mods
```

No-install:

```bash
codex mcp add rimworld-modder -- \
  dnx cryptiklemur.rimworld-modder-mcp \
  --yes \
  -- \
  --rimworld-path=/absolute/path/to/RimWorld \
  --mod-dirs=/absolute/path/to/RimWorld/Mods
```

</details>

<details>
<summary>Claude Code</summary>

Global installed tool:

```bash
claude mcp add --scope user rimworld-modder -- \
  rimworld-modder-mcp \
  --rimworld-path=/absolute/path/to/RimWorld \
  --mod-dirs=/absolute/path/to/RimWorld/Mods
```

No-install:

```bash
claude mcp add --scope user rimworld-modder -- \
  dnx cryptiklemur.rimworld-modder-mcp \
  --yes \
  -- \
  --rimworld-path=/absolute/path/to/RimWorld \
  --mod-dirs=/absolute/path/to/RimWorld/Mods
```

</details>

<details>
<summary>Goose CLI</summary>

Quick session with the installed tool:

```bash
goose session \
  --with-extension "rimworld-modder-mcp --rimworld-path=/absolute/path/to/RimWorld --mod-dirs=/absolute/path/to/RimWorld/Mods"
```

Quick session with no-install `dnx`:

```bash
goose session \
  --with-extension "dnx cryptiklemur.rimworld-modder-mcp --yes -- --rimworld-path=/absolute/path/to/RimWorld --mod-dirs=/absolute/path/to/RimWorld/Mods"
```

For a persistent Goose setup, use `goose configure` and add a command-line extension.

</details>

<details>
<summary>Claude Desktop, Cursor, Cline, Windsurf, Continue</summary>

If the client exposes stdio MCP config, use either:

- `command: "rimworld-modder-mcp"` with the args shown above
- `command: "dnx"` with the no-install block shown above

</details>

## Runtime-Only Fallback

If you only want the runtime, use the release bundle instead of the NuGet tool.

1. Install the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0/runtime).
2. Download the latest `rimworld-modder-mcp-vX.Y.Z-dotnet.zip` from [GitHub Releases](https://github.com/cryptiklemur/rimworld-modder-mcp/releases).
3. Extract it somewhere permanent.
4. Point your MCP client at `RimWorldModderMcp.dll`.

Bundle config:

```json
{
  "mcpServers": {
    "rimworld-modder": {
      "command": "dotnet",
      "args": [
        "/absolute/path/to/RimWorldModderMcp.dll",
        "--rimworld-path=/absolute/path/to/RimWorld",
        "--mod-dirs=/absolute/path/to/RimWorld/Mods"
      ]
    }
  }
}
```

Each bundle also includes:

- `manifest.json`
- `QUICKSTART.md`
- `examples/generic-mcp-config.posix.json`
- `examples/generic-mcp-config.windows.json`

## Docker

Build:

```bash
docker build -t rimworld-modder-mcp .
```

Run:

```bash
docker run \
  -i \
  --rm \
  -v "/path/to/rimworld:/rimworld:ro" \
  -v "/path/to/workshop:/workshop:ro" \
  rimworld-modder-mcp \
  --rimworld-path=/rimworld \
  --mod-dirs=/rimworld/Mods,/workshop
```

MCP config:

```json
{
  "mcpServers": {
    "rimworld-modder": {
      "command": "docker",
      "args": [
        "run",
        "-i",
        "--rm",
        "-v", "/path/to/rimworld:/rimworld:ro",
        "-v", "/path/to/workshop:/workshop:ro",
        "ghcr.io/cryptiklemur/rimworld-modder-mcp:latest",
        "--rimworld-path=/rimworld",
        "--mod-dirs=/rimworld/Mods,/workshop"
      ]
    }
  }
}
```

## Arguments

Required:

- `--rimworld-path` path to the RimWorld install

Common optional:

- `--mod-dirs` comma-separated mod directories
- `--mods-config-path` path to `ModsConfig.xml` if you only want enabled mods
- `--logPath` path to `Player.log`
- `--allowedDlcs` official-content target for compatibility checks, default `Core,Biotech`
- `--log-level` `Debug`, `Information`, `Warning`, or `Error`
- `--scopeType` and `--scopeValue` for `audit_scope`

Common RimWorld paths:

- Windows Steam: `D:\SteamLibrary\steamapps\common\RimWorld`
- Linux Steam: `~/.steam/steam/steamapps/common/RimWorld`
- macOS Steam: `~/Library/Application Support/Steam/steamapps/common/RimWorld`

Run `rimworld-modder-mcp --help` for the full argument list.

## Tool Highlights

Representative tools:

- `triage_player_log`
- `mod_ready_check`
- `scan_dlc_dependencies`
- `audit_scope`
- `validate_def_against_runtime`
- `triage_patch_conflicts`
- `analyze_mod_compatibility`
- `get_mod_dependencies`
- `get_patch_conflicts`
- `get_def_inheritance_tree`
- `compare_defs`
- `suggest_load_order`

Your MCP client can inspect the full tool list directly.

## Build From Source

```bash
git clone https://github.com/cryptiklemur/rimworld-modder-mcp.git
cd rimworld-modder-mcp
dotnet build src/RimWorldModderMcp/RimWorldModderMcp.csproj
dotnet run --project src/RimWorldModderMcp/RimWorldModderMcp.csproj -- --rimworld-path="/path/to/RimWorld"
```

## Release

`semantic-release` publishes:

- the NuGet `.NET tool`
- the `.nupkg` as a GitHub release asset
- the runtime-only zip bundle
- the `.sha256` checksum

Useful commands:

```bash
npm ci
npm run release:dry-run
npm run release:prepare-artifacts -- 1.2.5
```

Secrets used by CI:

- `NUGET_API_KEY` for NuGet publish
- `SEMANTIC_RELEASE_TOKEN` if you want release-created tags to trigger other workflows
