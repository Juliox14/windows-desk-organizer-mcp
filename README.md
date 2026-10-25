# DeskOrganizer

Windows desktop icon CLI and local MCP server (`net8.0-windows`). Supports inventory, custom layout previews and **experimental batch application and restoration**. The server starts in read-only mode. Writing tools require the local `--enable-writes` option. The legacy CLI retains its explicit movement commands.

## Install MCP + portable skill

Download a Windows x64 ZIP from [GitHub Releases](https://github.com/Juliox14/windows-desk-organizer-mcp/releases), extract the entire package, and follow [the installation guide](docs/INSTALL.md). The self-contained package includes the runtime, server, configuration examples and `skills/desk-organizer`. Copy that whole skill folder to your agent's discovery directory and configure its local stdio MCP connection. No resident application, cloud service or AI model API key is required by this server.

The [portable skill](skills/desk-organizer/SKILL.md) teaches the agent to inventory icons, classify them using the user's preference, preview exact positions, obtain approval, apply once, inspect outcomes and restore safely. Agent Skills clients differ in installation paths; the guide covers Codex and Claude Code and links their official instructions. The server's tool schema is usable without a skill as well.

Try: **"Organize my desktop by functionality with spaces between categories. Show me the preview first."** `spatial-groups` reserves empty grid space; it does not draw frames or persist category labels. Movement is opt-in and experimental.

## MCP server

Build with the .NET 8 or 9 SDK on Windows:

```powershell
dotnet restore DeskOrganizer.sln
dotnet build DeskOrganizer.sln --no-restore
```

Configure a local stdio MCP client to launch the built server directly. A typical `mcpServers` configuration is provided in [`examples/mcp.config.json`](examples/mcp.config.json); adapt its enclosing keys to your client. Replace the executable path with the absolute path on your computer:

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

For a development build, the executable is at `src/DeskOrganizer.Mcp/bin/Debug/net8.0-windows/DeskOrganizer.Mcp.exe`. Keep its accompanying DLLs and configuration files together. Run the built executable rather than a build command: stdout is reserved for MCP messages. Logs go to stderr; the server exits when the client closes stdin. Read-only tools write no inventory or layout files.

The server must run in the same interactive Windows session as Explorer. Desktop queries and a native preflight with no positioning call have been verified on Windows x64 with one monitor at 125% scaling. Queries run in Explorer's DPI awareness context, avoiding the mismatch between virtualized client dimensions and Shell positions. Writing currently requires one primary monitor, disabled auto-arrange, resolved Shell identities and valid changed destinations. Multiple-monitor writing and ARM64 execution are not supported. No administrator privileges are required in a normal user session. A sandbox that denies access to Explorer will return a tool error.

### Available tools

| Tool | Behavior |
| --- | --- |
| `get_desktop_state` | Returns icons, view coordinates, monitor observations, capabilities and an expiring snapshot. Does not expose full paths, shortcut targets or Shell parsing-name hashes. |
| `preview_layout` | Validates a snapshot against a fresh desktop observation and returns a hypothetical plan. Strategies: `name-ascending`, `usage-category`, `custom`, `spatial-groups`, `category-rows`. |
| `apply_layout` | With `--enable-writes`: applies an exact cached plan and journals a verified result under a caller-supplied operation ID. |
| `get_operation` | With `--enable-writes`: reads a durable operation result without repeating its movement. |
| `preview_restore_layout` | With `--enable-writes`: creates a restoration plan from a backup and a fresh snapshot; rejects subsequent edits. |
| `restore_layout` | With `--enable-writes`: executes an exact restoration preview as a separately journaled operation. |

Snapshots last five minutes, are local to one server process and are limited to the eight most recent captures. Icon references are random and scoped to their snapshot; they are not persistent identities. A changed desktop, identity or geometry invalidates a preview. Queries support up to 2,048 icons and labels of up to 1,024 characters. Custom previews accept at most 64 groups with unique labels of up to 128 characters.

To organize by an arbitrary preference, the agent first calls `get_desktop_state`, then sends groups with the returned references:

```json
{
  "snapshotId": "<snapshotId from get_desktop_state>",
  "strategy": "custom",
  "groups": [
    { "name": "Development", "iconRefs": ["<editor iconRef>", "<terminal iconRef>"] },
    { "name": "Communication", "iconRefs": ["<chat iconRef>"] }
  ]
}
```

For `custom`, group order and reference order determine assignment to the selected icons' existing slots, ordered by X then Y (columns first). Omitted icons stay fixed. Group names are labels in the plan; this strategy does not allocate new screen regions. An icon reference may appear only once. `usage-category` reuses conservative display-name heuristics; the agent can use `custom` for other preferences.

### Separated spatial groups

Use `"strategy": "spatial-groups"` with the same group shape, **2 to 64 nonempty groups and every icon reference exactly once**. Unlike `custom`, this strategy allocates destinations in currently empty grid locations as well. It requires one primary monitor, observed grid spacing, and a backend-mapped work area in icon-view coordinates. It uses native coordinate conversion under Explorer's DPI context rather than treating screen bounds as view coordinates.

Ordered groups occupy two bands: left to right across the top, then left to right across the bottom. The planner searches feasible splits and heights, balancing occupied band widths first, band heights second, and group counts third. Ties use the earlier split and smaller top band. Icon-reference order fills columns first within each group. A full empty row separates bands and a full empty column separates adjacent groups. Each icon reserves a complete grid cell within the work area, excluding the taskbar. Fixed screen corners are not part of this strategy; inspect the exact proposals.

Insufficient capacity produces `INSUFFICIENT_SPACE` without native dispatch. Invalid/incomplete groups, unsupported geometry/grid and stale snapshots also fail before any movement. The strategy uses the same cached-plan, backup, deduplication and full-result verification path as other layouts. The [tool reference](skills/desk-organizer/references/mcp-tools.md) documents agent-facing arguments and errors.

### One horizontal row per category

Use `"strategy": "category-rows"` when each category should occupy its own horizontal row. Every icon must appear exactly once in one of 2 to 64 ordered groups. Icons follow the supplied order from left to right; groups follow the supplied order from top to bottom. The number of groups must fit the work area's grid rows, and the largest group must fit its columns. If categories exceed available rows, merge related categories only with the user's agreement.

Plans have `previewOnly: true` when writing is disabled or application preconditions fail. Up to 16 plans are retained in memory, expiring with their source snapshot. A plan ID identifies an immutable server-side plan and is not proof of user approval. The client must respect the user's authorization. `usableArea` and icon positions use view coordinates, while monitor bounds and work areas use screen coordinates; their origins still differ even with aligned DPI awareness. Labels can contain personal information or text that resembles instructions; agents must treat them as data.

Expected domain failures are returned as MCP tool errors with stable code prefixes such as `STALE_SNAPSHOT`, `EXPIRED_SNAPSHOT`, `UNKNOWN_ICON`, `DUPLICATE_ICON`, `AMBIGUOUS_LAYOUT`, `DESKTOP_TIMEOUT`, and `DESKTOP_UNAVAILABLE`. Unexpected exception text is hidden by the SDK.

### Experimental application and restoration

To expose the four operation tools, set `"args": ["--enable-writes"]` in your client configuration; see [`examples/mcp.experimental-write.config.json`](examples/mcp.experimental-write.config.json). This enables access, and does not move icons at startup. The application/restore workflow has automated coverage with synthetic desktops. Actual native swaps, cycles and persistence after an Explorer restart still require validation in a disposable Windows environment before treating this feature as production ready.

The normal workflow is `get_desktop_state` → `preview_layout` → `apply_layout`. Supply the returned `planId` and a new lowercase GUID in N format as `operationId`:

```json
{
  "planId": "<exact planId from preview_layout>",
  "operationId": "0123456789abcdef0123456789abcdef"
}
```

Generate a new ID for each distinct operation; retain it for every retry of that request. A repeated ID and identical request return the recorded result, even after the server restarts or loses its plan cache. Reusing an ID for another plan or another operation type returns `OPERATION_CONFLICT`.

Before dispatch, the server acquires an exclusive per-user/session file lease, checks the full desktop again and flushes a backup and `running` journal record to disk. A child process resolves every icon by its Shell identity and rechecks the desktop before one `IFolderView::SelectAndPositionItems` batch. This supports permutations without a temporary screen slot; it is not an atomic transaction. The parent reads the whole desktop until it obtains two matching observations, checks intended and untouched icons, and saves the final result. No automatic movement retry occurs.

Child processes run without windows and are bound to the parent by a Windows job with kill-on-close. Both reads and writes have a 25-second child deadline, bounded pipe payloads, and one active worker per server. The same executable has private worker entry points; no second executable needs to be packaged. Ending a worker cannot undo a request already delivered to Explorer.

| Operation status | Meaning |
| --- | --- |
| `running` | A durable start record exists and the writer is active. After an interrupted server this may become `unknown`. |
| `applied` | The complete observed result matched the plan. This does not promise persistence after restart. |
| `unchanged` | All requested positions were already present; no native movement was dispatched. |
| `rejected` | The worker reported a definite rejection before native movement dispatch. |
| `partial` | A completed call produced a stable layout that differs from the plan. Normal organization is blocked; a validated restoration can recover it. |
| `unknown` | A call or durable completion could not be verified. Retain the operation ID and inspect the desktop. Writing is blocked until the previous Explorer **process instance** ends, not merely until a view or window changes. |

`changedCount` is the number of changes requested by the plan, not a claim that every change succeeded in a partial/unknown result. After native dispatch, invocation cancellation does not cancel verification while the server remains alive. Host termination can leave a durable start record with an unknown outcome; the lifetime job terminates its child worker. Query `get_operation` using the same ID after reconnecting. The program never restarts Explorer to clear an uncertain call. A fresh request can reconcile a record from a confirmed earlier Explorer process; its historical outcome remains recorded.

To restore, capture a new state, call `preview_restore_layout` with the prior `backupId` and the new `snapshotId`, then call `restore_layout` with that restoration plan ID and a new operation ID. Restoration verifies identities, topology and the recorded post-operation layout; it rejects intervening edits, deleted icons and geometry changes. It creates its own backup. It does not blindly roll back or overwrite changes made since the recorded result. If an unknown result cannot be matched to either its backup or intended result, automatic restoration is rejected.

Backups and journals live in `%LOCALAPPDATA%\DeskOrganizer\sessions\<WindowsSessionId>\operations`. They contain icon names, positions and private Shell fingerprints; treat this directory as personal data. Records are retained locally with a 256-record limit; no upload or automatic pruning occurs. Archive terminal history manually when it is no longer needed, retaining unresolved operations and needed backups. Removing a record also removes its replay protection and backup reference. If the durable journal is unavailable or corrupt, writing fails closed.

### Build a distributable Windows x64 package

```powershell
./scripts/Publish-Package.ps1 -Version 0.1.0-preview
python tests/package_smoke.py artifacts/packages/desk-organizer-mcp-0.1.0-preview-win-x64.zip
# Optional interactive verification: inventory and preview only; never moves icons.
python tests/package_smoke.py artifacts/packages/desk-organizer-mcp-0.1.0-preview-win-x64.zip --desktop
```

The script produces a ZIP and SHA256 sidecar under `artifacts/packages`, with `server/DeskOrganizer.Mcp.exe`, the portable skill, examples and installation guide. It refuses to overwrite an existing archive. It publishes self-contained and untrimmed to retain reflection/COM/SDK behavior. The smoke check extracts to a fresh temporary directory, hides installed .NET from the child environment, initializes MCP and checks that writing tools are absent. `--desktop` additionally previews spatial groups and checks that actual positions remain unchanged. It never calls a write tool.

The [release workflow](.github/workflows/release.yml) builds and runs synthetic checks on Windows before packaging and protocol validation. A manual workflow run attaches artifacts without publishing. Pushing a `v*` tag publishes a GitHub **prerelease** with ZIP/checksum assets only after checks pass. Build artifacts are ignored by Git. The project targets .NET 8; review its support lifecycle before promoting a production release. Do not claim native batch persistence is verified by the synthetic/protocol checks.

To publish after committing and pushing the reviewed source, choose an unused version tag and push it, for example:

```powershell
git tag v0.1.0-preview
git push origin v0.1.0-preview
```

The workflow creates the downloadable GitHub release; creating a local ZIP alone does not publish it. For a dry run, use **Actions > Windows MCP package > Run workflow** and download its artifact.

## Project structure and checks

- `DeskOrganizer.csproj`, `Program.cs`: compatibility CLI and explicit legacy state-changing commands.
- `src/DeskOrganizer.Core`: typed models, snapshot/plan cache, deterministic planning, operation state machine and durable journal, without Windows API calls.
- `src/DeskOrganizer.Windows`: Shell inventory, DPI context, batch movement, worker protocol/lifetime isolation and the legacy CLI adapter. The MCP backend does not open Explorer's process memory.
- `src/DeskOrganizer.Mcp`: stdio host with explicit read-only and opt-in writing tool registration.
- `tests/DeskOrganizer.Tests`: checks for planning, execution/recovery, journal faults, privacy, native signatures, worker lifetimes and MCP stdio using synthetic fixtures. No actual desktop changes.
- `skills/desk-organizer`: portable Agent Skill and on-demand MCP reference.
- `scripts/Publish-Package.ps1`, `docs/INSTALL.md`: Windows package creation and end-user setup.

After building the solution, run:

```powershell
dotnet run --project tests/DeskOrganizer.Tests/DeskOrganizer.Tests.csproj --no-build
```

The existing CLI regression checks and the opt-in live MCP check require an accessible interactive Explorer session. Both inspect the actual desktop without moving icons. The live check also exercises the native worker with identical source and target positions, asserting that no positioning call is submitted:

```powershell
python tests/snapshot_cli.py
dotnet run --project tests/DeskOrganizer.Tests/DeskOrganizer.Tests.csproj --no-build -- --live-readonly
```

Synthetic stdio fixtures are provided only by the test executable; the production server has no fixture mode. Local desktop captures under `odd/tasks/` are ignored by Git. The remaining implementation stages and their acceptance criteria are recorded in [`MCP_READINESS_PLAN.md`](MCP_READINESS_PLAN.md).

## CLI

Run `DeskOrganizer.exe --help` for the English command list. The older Spanish command names remain available as compatibility aliases; their output messages are now English. The no-argument CLI startup retains its legacy export and key prompt; launch the separate MCP executable for agent connections.

## Read-only commands

- `DeskOrganizer.exe list-icons` prints a JSON array with `index`, `name`, `x`, and `y`. Add `--with-shell-ids` to include `shellId` and `hasFileSystemPath` when a Shell item matches a ListView item uniquely by exact display name and position. The option rereads the desktop before and after the Shell query and refuses a changed layout; ambiguous or missing matches get `null` for both identity fields. It does not move icons or write files. An index is **not** a stable icon identity. The option does not change default or legacy JSON fields.
- `DeskOrganizer.exe capture-snapshot` prints `snapshotId` and `icons` (the same English icon fields). `DeskOrganizer.exe verify-snapshot <snapshotId>` returns `{"unchanged":true}` and exit code 0 for a match, `{"unchanged":false}` and exit code 2 for a mismatch; invalid input or a read failure writes an English error to stderr and exits with code 1. Both commands read the desktop twice and fail if it changes between reads. A snapshot is **not** a permanent icon ID or authorization token: replacing an icon with indistinguishable visible data cannot be detected.
- `DeskOrganizer.exe probe-shell-identities` is a read-only Shell COM experiment. It returns the Shell view index, display name, position, a SHA-256 fingerprint of the Shell parsing name (`shellId`) or `null` when ambiguous/unavailable, and `hasFileSystemPath` (without returning the path). View indices are not identities. These fingerprints are not secrets, authorization tokens, or guaranteed to survive a rename; the legacy single-icon command does not consume them; the MCP batch worker resolves equivalent fingerprints internally. Filesystem-less entries may be virtual Shell items, but the flag alone does not establish their kind. The probe does not intentionally write files or alter icon positions. Its JSON **can contain personal information**: display names are included and unsalted, repeatable hashes of parsing names can be guessed. Do not share it as anonymized data. The 10-second in-process timeout reports a failure but cannot forcibly cancel a blocked Shell COM call. Neither duplicate display-name mapping nor identity continuity after an Explorer restart has been demonstrated; those need isolated Windows fixtures and a separately authorized restart.
- `DeskOrganizer.exe query-desktop-geometry` prints a JSON object for the **desktop icon ListView**, not application windows. `gridSpacing` is the current ListView cell spacing in its coordinate system (`x`, `y`), or `null` if not reported. `usableArea` is the ListView client rectangle in **client coordinates**; each entry in `monitors` has virtual-desktop `bounds`, a system-reported `workArea` (taskbar/reserved area excluded), and `primary`. These rectangles are not interchangeable coordinate systems, nor a promise that every work-area pixel accepts an icon. `autoArrange` reads the current ListView style and can be `null` if the style cannot be read; it does not use or change the registry. `windowDpi` comes from the desktop ListView window (or `null` if unavailable), while `monitorDpi` is always `null`: the window's DPI does not establish each monitor's physical DPI or scale. DPI awareness may virtualize reported coordinates; do not interpret them as physical pixels or use this query to move icons. Monitor enumeration failures return an error rather than a partial layout. The command does not change icon positions or write files.
- `DeskOrganizer.exe probe-desktop-coordinates` returns `threadDpiAwareness` (the query thread), `windowDpiAwareness` (Explorer's desktop icon ListView), `clientScreenRect` (the ListView client corners mapped through `ClientToScreen`), monitor `bounds`/`workArea`/`primary`, and `overlappingMonitorIndices` (indices whose reported bounds numerically overlap that rectangle). Awareness is `unaware`, `system`, `perMonitor`, or `null` if not reported; `clientScreenRect` and overlap indices are `null` when mapping fails. Coordinates reflect the query thread's Windows coordinate virtualization and are **not proven physical pixels**; a numeric overlap is not proof of DPI conversion or icon placement. The desktop ListView may have a different DPI awareness from the calling thread. Do not multiply by `windowDpi / 96` or combine ListView icon positions with monitor bounds to move icons. The command reads only; validating mixed-DPI or multi-monitor behavior requires a separately available setup and read-only checks.
- `DeskOrganizer.exe diagnose-desktop-layout` prints a read-only JSON summary: `iconCount`, `outsideClientAreaCount`, `outsideClientAreaPositions` (`x`, `y` only; no names), and `note`. "Outside" means the observed icon position lies outside the desktop ListView's **reported client rectangle** at query time, not that Windows placed it incorrectly, the icon is invisible, or that the position is invalid. The command checks the layout and client rectangle for changes during its reads and fails rather than returning an inconsistent summary. It does not write files or move icons; it leaves other commands' JSON unchanged. Positions can still reveal information about your layout, so review the output before sharing.
- `DeskOrganizer.exe preview-icon-layout` prints a hypothetical alphabetical plan as JSON, **without moving icons or writing files**. It sorts icon display names with ordinal case-insensitive comparison (ordinal spelling and current index break ties), then assigns them only the currently occupied positions ordered by `x`, then `y` (columns first). The output contains `snapshotId`, `strategy: "name-ascending"`, `iconCount`, `changedCount`, and `proposals` with `index`, `name`, `fromX`, `fromY`, `toX`, `toY`, `changed`. A proposed position may still be outside the reported client area: this is an observed slot, **not** a promise that Windows will allow or retain movement there. `snapshotId` identifies the observed layout for comparison, not permanent item identity or permission to execute the plan. Indices may change, duplicate names are not disambiguated across restarts, and active auto-arrange may override any future movement. Do not feed this JSON directly to `move-icon`. Display names and positions may be personal; review before sharing.
- `DeskOrganizer.exe preview-category-layout` prints a read-only **usage-based** grouping preview using only the currently occupied positions. Groups appear in this order: `development`, `communication`, `media`, `games`, `utilities`, `documents`, `other`; names within each group are sorted ordinally, case-insensitively. A small list of explicitly recognized application names maps to usage groups (for example, `Visual Studio Code` to `development`, `Zoom Workplace` to `communication`, `VLC media player` to `media`, and `Steam` to `games`); `.pdf`, `.docx`, and `.txt` names map to `documents`. All other names, including ambiguous personal folders, remain in `other`; these are **heuristics based on display names**, not verified file type, shortcut target or actual usage. Inspect the `category` of each proposal before treating this as your preferred classification. The JSON adds `categories` (group name and count) and includes `category` on each proposal; `snapshotId`, `strategy`, `iconCount`, `changedCount`, `index`, `name`, `fromX`, `fromY`, `toX`, `toY`, and `changed` follow the alphabetical preview's meanings. It never moves icons or changes existing command JSON; output may contain personal names.
- `DeskOrganizer.exe count-icons` prints the number of icons; `DeskOrganizer.exe get-desktop-handle` prints the desktop ListView handle. Both are read-only. The older `contar-iconos` alias retains its original handle-only console output.

## Commands that change state

- `DeskOrganizer.exe export-icon-positions` writes `desktop_icons.json` to the desktop. It may contain personal information; review it before sharing. The old `leer-posiciones` alias and the no-argument startup behavior still write the legacy `datos_entrenamiento.json` with the original JSON field names. This file is **not** created by the read-only commands. The debug launch profile now runs `list-icons` instead.
- `DeskOrganizer.exe move-shell-icon <snapshotId> <index> <X> <Y>` changes **one** desktop icon position using its Shell child PIDL, not its ListView index for the final move. It requires a fresh `capture-snapshot` ID, disabled auto-arrange, a uniquely named source item, and a vacant target position. It returns `{"moved":true,"snapshotId":"..."}` only after reading back the requested position and confirming every other name/position pair is unchanged. A failure or timeout may mean the move still occurred: inspect the desktop before any retry. Never use it to blindly replay `preview-icon-layout` without checking each step; this command does not guarantee persistence after Explorer restart or solve duplicate-name identity. `DeskOrganizer.exe move-icon <index> <X> <Y>` remains the legacy unguarded ListView mover; `disable-auto-arrange` can change the registry and restart Explorer. Neither legacy command is ready to be exposed to an AI agent. The legacy `move-icon` implementation sends `LVM_SETITEMPOSITION`; [Microsoft notes](https://devblogs.microsoft.com/oldnewthing/20211122-00/?p=105948) that direct ListView position changes might not persist on modern Windows. The experimental MCP batch mover uses the supported Shell [`IFolderView::SelectAndPositionItems`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview-selectandpositionitems) API and verify the observed result.

Legacy read aliases (`listar-iconos`, `capturar-snapshot`, `verificar-snapshot`) keep their previous JSON field names (`indice`, `nombre`), while the new commands use English fields. The legacy `IconoData` DTO also remains for compatibility; new code uses `DesktopIcon`. Legacy CLI commands remain separate from the experimental MCP execution path. Native batch movement and persistence still require controlled Windows validation before production use.
# windows-desk-organizer-mcp
