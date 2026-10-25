# Install DeskOrganizer MCP + skill

DeskOrganizer lets a local agent inventory and organize Windows desktop icons. Separated groups leave empty grid space between categories. It does not draw fences or run a resident application.

## 1. Download the Windows package

Download the `desk-organizer-mcp-<version>-win-x64.zip` asset from [GitHub Releases](https://github.com/Juliox14/windows-desk-organizer-mcp/releases). Extract the entire ZIP to a permanent directory, for example `C:\Tools\DeskOrganizer`. Keep the `server` directory and its files together. The self-contained Windows x64 package includes .NET; the user does not need the SDK or a separately installed runtime.

Run the MCP client locally in the same Windows user/session as Explorer. One primary monitor is supported for writing and spatial groups. Disable Windows desktop **View > Auto arrange icons** manually before enabling movement. This does not require administrator access. Remote/cloud-only agents and WSL-only servers cannot directly operate this interactive Windows desktop.

## 2. Configure a local stdio MCP server

Use `server\DeskOrganizer.Mcp.exe` from the extracted package as the command. Use an absolute path, including when the agent opens an unrelated project. Do not use the legacy CLI executable or `dotnet run` as the server command. The server remains alive while the client keeps stdin open; starting and immediately closing it without an MCP client does not validate a connection.

For clients using `mcpServers` JSON, merge this entry into their MCP configuration:

```json
{
  "mcpServers": {
    "desk-organizer": {
      "command": "C:\\Tools\\DeskOrganizer\\server\\DeskOrganizer.Mcp.exe",
      "args": []
    }
  }
}
```

For **Codex**, merge this into `%USERPROFILE%\.codex\config.toml` (also included in `examples/codex.config.toml`):

```toml
[mcp_servers.desk-organizer]
command = 'C:\Tools\DeskOrganizer\server\DeskOrganizer.Mcp.exe'
args = []
```

For **Claude Code running on Windows**, register it through PowerShell:

```powershell
claude mcp add --transport stdio --scope user desk-organizer -- 'C:\Tools\DeskOrganizer\server\DeskOrganizer.Mcp.exe'
```

Other clients use different configuration locations. Match their local **stdio** server format and use the same command/arguments. Desktop access may require client-specific sandbox permissions. See official [Codex MCP configuration](https://developers.openai.com/codex/mcp/) and [Claude Code MCP configuration](https://code.claude.com/docs/en/mcp).

## 3. Install the portable skill

The package includes `skills/desk-organizer/SKILL.md` and its `references` folder. Copy the **entire** `desk-organizer` folder into a skill discovery location supported by your agent. Do not copy only the Markdown entrypoint.

| Client | Personal skill location on Windows | Invocation |
| --- | --- | --- |
| Codex | `%USERPROFILE%\.agents\skills\desk-organizer` | Ask to organize the desktop, or invoke `$desk-organizer`. |
| Claude Code | `%USERPROFILE%\.claude\skills\desk-organizer` | Ask to organize the desktop, or invoke `/desk-organizer`. |
| Other Agent Skills clients | Their documented skill directory | Their documented discovery/invocation method. |

Repository-scoped alternatives are `.agents/skills/desk-organizer` for Codex and `.claude/skills/desk-organizer` for Claude Code. A personal installation makes the skill available outside a particular repository. Restart/reload the client after setup as its documentation requires. Installing a skill does not register the MCP server or grant writing permission. See [Agent Skills format](https://agentskills.io/specification), [Codex skill locations](https://developers.openai.com/codex/skills/), and [Claude Code skills](https://code.claude.com/docs/en/skills).

## 4. Verify the read-only connection

Ask: **"Use Desk Organizer to list my desktop icons and preview groups by functionality. Do not apply the layout."**

The client should discover `get_desktop_state` and `preview_layout`. The preview should contain fresh snapshot references, complete destinations and `previewOnly: true`. Previewing creates no inventory files and moves no icons. A missing connection or skill should be resolved through client setup; agents should not substitute shell movers.

## 5. Enable movement deliberately

Writing is experimental. The server supports verified batches, backups and recovery records; it cannot guarantee placement survives Explorer/system restarts. Start in read-only mode and review a preview first.

To expose the four operation tools, replace `args: []` with `args: ["--enable-writes"]` in JSON, or `args = ["--enable-writes"]` in TOML, then reconnect the client. For a CLI registration, reconfigure its server arguments using the client's instructions. Do not add a second entry for the same server.

Writing tools are `apply_layout`, `get_operation`, `preview_restore_layout`, and `restore_layout`. Enabling them does not move icons. The skill asks for explicit approval of the exact preview before applying or restoring it. The MCP host remains the enforcement point for stale state, identities, geometry, deduplication and recovery.

Ask: **"Organize my desktop by functionality with spaces between categories. Show me the preview first."** The agent can classify icons into 2 to 64 nonempty groups, include every icon exactly once, and use `spatial-groups`. Inspect actual destinations: the automatic split into top/bottom bands adapts to capacity and does not guarantee fixed corner positions. Too little room returns `INSUFFICIENT_SPACE` before movement. `custom` can instead reorder selected icons in existing slots while keeping omitted icons fixed.

Retain the returned `operationId` and `backupId`. After a disconnect or `partial`/`unknown` result, inspect the same operation before doing more work. To restore, ask for a restoration preview from that backup and approve it; intervening edits or changed geometry may prevent automatic restoration.

## Local data and removal

Read-only calls write no desktop inventory/layout files. Applied operations store private names, positions and Shell fingerprints under `%LOCALAPPDATA%\DeskOrganizer\sessions\<WindowsSessionId>\operations`. The journal has a 256-record limit. Keep unresolved operations and needed backups; deleting records removes their backup and replay protection. Do not share this directory as diagnostic data without reviewing it.

To remove the integration, remove its MCP client entry, remove the installed skill folder, and remove the extracted package. Keep any backups you still need. Nothing installs at login or as a Windows service.
