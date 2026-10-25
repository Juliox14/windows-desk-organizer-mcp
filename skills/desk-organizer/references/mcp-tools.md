# DeskOrganizer MCP tools

All calls use local stdio MCP. Arguments below are unprefixed tool names; adapt to the names exposed by the client. The server defaults to read-only; the four operation tools are registered only with local `--enable-writes`.

| Tool | Arguments | Result and side effects |
| --- | --- | --- |
| `get_desktop_state` | None | `snapshotId`, `expiresAt`, `icons` (`iconRef`, `name`, `position`, `identityResolved`), geometry, capabilities, warnings. No movement or files. |
| `preview_layout` | `snapshotId`; optional `strategy` (default `name-ascending`); `groups` for custom/spatial/category-row strategies | Cached `planId`, expiration, complete proposals (`iconRef`, `name`, `group`, `from`, `to`, `changed`), `changedCount`, `previewOnly`, warnings. No movement or files. |
| `apply_layout` | `planId`, `operationId` | Applies an exact organization preview, records a backup and verified status. Requires approval and enabled writing. |
| `get_operation` | `operationId` | Persistent result, no movement. Use after reconnecting or uncertain outcomes. |
| `preview_restore_layout` | `backupId`, fresh `snapshotId` | New restoration preview. Rejects intervening layout/identity/geometry changes. |
| `restore_layout` | Restoration `planId`, new `operationId` | Applies the approved restoration as a separately journaled operation. |

## Spatial groups

Call `get_desktop_state`, categorize every returned icon, and use their actual references:

```json
{
  "snapshotId": "<snapshotId from this server session>",
  "strategy": "spatial-groups",
  "groups": [
    { "name": "Development", "iconRefs": ["<editor ref>", "<terminal ref>"] },
    { "name": "Communication", "iconRefs": ["<chat ref>"] },
    { "name": "Other", "iconRefs": ["<every remaining ref>"] }
  ]
}
```

The example is schematic: do not send placeholder references. Supply 2 to 64 nonempty, uniquely named groups. Names are 1 to 128 characters, with no leading/trailing whitespace. Every icon must occur exactly once. References are opaque; do not use display names, paths, or CLI indices instead.

The server maps the primary monitor work area into icon-view coordinates using Explorer's DPI context, aligns with the observed grid, and reserves a full cell per icon. It partitions the ordered groups into two bands. It balances required band widths, then band heights, then group counts; group and icon-reference order are preserved within each band. Each band fills columns first. A complete empty grid row separates bands; a complete empty column separates adjacent groups. The split adapts to capacity. Review exact proposals to describe locations; the first/last group is not automatically anchored to a screen corner.

`custom` accepts the same group shape with 1 to 64 groups, but may omit icons and reuses selected occupied slots instead of allocating new areas. `name-ascending` and `usage-category` do not accept `groups`. All strategies return proposals for the whole desktop.

## One row per category

Use `"strategy": "category-rows"` with complete groups when each category should occupy a horizontal row. The group array sets top-to-bottom category order; each group's `iconRefs` sets left-to-right order. The planner checks that every row and column fits the mapped work area and fails before execution otherwise. If categories outnumber available rows, ask the user before merging categories.

## Apply and inspect

After approval, use the returned plan ID and a fresh lowercase GUID in N format:

```json
{ "planId": "<approved planId>", "operationId": "0123456789abcdef0123456789abcdef" }
```

Generate a different ID for each distinct request; never reuse the example literal. Retain an ID across inspection/reconnection of that request. A plan and its icon references live only in the server session; an operation and its backup persist in the local per-user/Windows-session journal. A replay with the same plan, operation type and operation ID returns the recorded result, even after plan expiry or restart. A different request with the same ID is rejected.

| Status | Meaning and response |
| --- | --- |
| `applied` | Full observed result matches the plan. Report completion and keep `backupId`. |
| `unchanged` | No native positioning was needed. |
| `rejected` | Definite rejection before native dispatch. Inspect the reason before preparing a new request. |
| `running` | Started and writer active. Inspect the same ID; do not start another operation. |
| `partial` | Stable result differs from the plan. Stop organization; offer a restoration preview if supported. |
| `unknown` | Completion could not be established. Stop and retain the ID. Recovery may require the prior Explorer process to end; do not terminate it automatically. |

A native batch is not an atomic transaction. `changedCount` counts requested changes, not confirmed changes in a partial/unknown outcome. The server verifies the complete layout and stores outcomes locally. It does not guarantee persistence after an Explorer or system restart.

## Domain errors

- `STALE_SNAPSHOT`, `EXPIRED_SNAPSHOT`, `UNKNOWN_SNAPSHOT`, `STALE_PLAN`, `EXPIRED_PLAN`, `UNKNOWN_PLAN`: capture again and preview; review changes before applying.
- `UNKNOWN_ICON`, `DUPLICATE_ICON`, `INCOMPLETE_GROUPS`, `INVALID_ARGUMENT`: correct references or groups using fresh state if needed.
- `INSUFFICIENT_SPACE`: the requested separation cannot fit. Ask before merging categories contrary to the user's preference or changing desktop settings.
- `UNSUPPORTED_GRID`, `UNSUPPORTED_GEOMETRY`, `AUTO_ARRANGE_ENABLED`, `WRITES_DISABLED`: report the unmet prerequisite; do not bypass it.
- `RECOVERY_REQUIRED`, `RESTORE_CONFLICT`, `JOURNAL_UNAVAILABLE`, `JOURNAL_CORRUPT`, `HISTORY_LIMIT`: stop writes and explain the concrete issue. Do not delete journal files or bypass conflicts to continue.
- `DESKTOP_UNAVAILABLE`, `DESKTOP_TIMEOUT`: report lack of desktop access. After a possibly dispatched write, inspect its operation ID before any further action.
