"""Read-only public CLI smoke tests; run with Explorer available on Windows.

Set DESKORGANIZER_DLL to the built application DLL and DOTNET to the dotnet executable.
No test invokes desktop-moving or export commands.
"""
import json
import os
import shutil
import subprocess
import unittest
from pathlib import Path

DOTNET = os.getenv("DOTNET") or shutil.which("dotnet.exe") or shutil.which("dotnet")
DLL = Path(os.getenv("DESKORGANIZER_DLL", "bin/Debug/net8.0-windows/DeskOrganizer.dll"))


class SnapshotCliTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not DOTNET or not DLL.is_file():
            raise RuntimeError("Build the Windows application first and set DOTNET if needed")

    def call(self, *arguments):
        return subprocess.run(
            [DOTNET, str(DLL), *arguments], text=True, capture_output=True, timeout=15
        )

    def test_snapshot_and_verification(self):
        captured = self.call("capturar-snapshot")
        self.assertEqual(0, captured.returncode, captured.stderr)
        self.assertEqual("", captured.stderr)
        result = json.loads(captured.stdout)
        self.assertRegex(result["snapshotId"], r"^[0-9a-f]{64}$")
        self.assertEqual({"snapshotId", "icons"}, set(result))
        self.assertEqual("\n", captured.stdout[-1:])
        self.assertIsInstance(result["icons"], list)
        self.assertTrue(all(set(icon) == {"indice", "nombre", "x", "y"} for icon in result["icons"]))

        checked = self.call("verificar-snapshot", result["snapshotId"])
        self.assertEqual(0, checked.returncode, checked.stderr)
        self.assertEqual("", checked.stderr)
        self.assertEqual('{"unchanged":true}\n', checked.stdout)

        changed = "0" * 64 if result["snapshotId"] != "0" * 64 else "1" * 64
        rejected = self.call("verificar-snapshot", changed)
        self.assertEqual(2, rejected.returncode)
        self.assertEqual("", rejected.stderr)
        self.assertEqual('{"unchanged":false}\n', rejected.stdout)

        invalid = self.call("verificar-snapshot", "not-a-snapshot")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Usage: verify-snapshot [64-character hexadecimal snapshotId]\n", invalid.stderr)

    def test_english_commands_and_legacy_compatibility(self):
        listed = self.call("list-icons")
        self.assertEqual(0, listed.returncode, listed.stderr)
        self.assertEqual("", listed.stderr)
        icons = json.loads(listed.stdout)
        self.assertTrue(all(set(icon) == {"index", "name", "x", "y"} for icon in icons))

        captured = self.call("capture-snapshot")
        self.assertEqual(0, captured.returncode, captured.stderr)
        self.assertEqual("", captured.stderr)
        snapshot = json.loads(captured.stdout)
        self.assertEqual({"snapshotId", "icons"}, set(snapshot))
        self.assertTrue(all(set(icon) == {"index", "name", "x", "y"} for icon in snapshot["icons"]))
        verified = self.call("verify-snapshot", snapshot["snapshotId"])
        self.assertEqual(0, verified.returncode, verified.stderr)
        self.assertEqual('{"unchanged":true}\n', verified.stdout)
        self.assertEqual("", verified.stderr)

        invalid = self.call("verify-snapshot", "invalid")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Usage: verify-snapshot [64-character hexadecimal snapshotId]\n", invalid.stderr)

        counted = self.call("count-icons")
        self.assertEqual(0, counted.returncode)
        self.assertEqual("", counted.stderr)
        self.assertRegex(counted.stdout, r"^[0-9]+\n$")

        legacy_count = self.call("contar-iconos")
        self.assertEqual(0, legacy_count.returncode)
        self.assertEqual("", legacy_count.stderr)
        self.assertIn("SUCCESS: Found SysListView32 HWND", legacy_count.stdout)

        help_result = self.call("--help")
        self.assertEqual(0, help_result.returncode)
        self.assertEqual("", help_result.stderr)
        self.assertIn("list-icons", help_result.stdout)
        self.assertNotIn("Comandos:", help_result.stdout)

        missing_move = self.call("mover-icono")  # Legacy alias: never moves without coordinates.
        self.assertEqual(0, missing_move.returncode)
        self.assertEqual("ERROR: Missing arguments. Usage: DeskOrganizer.exe move-icon [index] [X] [Y]\n", missing_move.stdout)
        self.assertEqual("", missing_move.stderr)

        unknown = self.call("unknown-command")
        self.assertEqual(0, unknown.returncode)
        self.assertEqual("ERROR: Unknown command 'unknown-command'. Run --help for available commands.\n", unknown.stdout)
        self.assertEqual("", unknown.stderr)

    def test_opt_in_shell_ids_preserves_default_output(self):
        enhanced = self.call("list-icons", "--with-shell-ids")
        self.assertEqual(0, enhanced.returncode, enhanced.stderr)
        self.assertEqual("", enhanced.stderr)
        icons = json.loads(enhanced.stdout)
        self.assertTrue(all(set(icon) == {"index", "name", "x", "y", "shellId", "hasFileSystemPath"} for icon in icons))
        resolved = [icon["shellId"] for icon in icons if icon["shellId"] is not None]
        self.assertEqual(len(resolved), len(set(resolved)))
        self.assertTrue(all(icon["hasFileSystemPath"] is None if icon["shellId"] is None else isinstance(icon["hasFileSystemPath"], bool) for icon in icons))
        self.assertEqual("\n", enhanced.stdout[-1:])

        invalid = self.call("list-icons", "--with-shell-ids", "unexpected")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Could not read desktop icons: Usage: list-icons [--with-shell-ids]\n", invalid.stderr)

        default = self.call("list-icons")
        self.assertEqual(0, default.returncode, default.stderr)
        self.assertEqual("", default.stderr)
        self.assertTrue(all(set(icon) == {"index", "name", "x", "y"} for icon in json.loads(default.stdout)))

        legacy = self.call("listar-iconos", "--with-shell-ids")
        self.assertEqual(0, legacy.returncode, legacy.stderr)
        self.assertEqual("", legacy.stderr)
        self.assertTrue(all(set(icon) == {"indice", "nombre", "x", "y"} for icon in json.loads(legacy.stdout)))

    def test_shell_identity_probe_is_read_only_and_repeatable(self):
        first = self.call("probe-shell-identities")
        self.assertEqual(0, first.returncode, first.stderr)
        self.assertEqual("", first.stderr)
        observed = json.loads(first.stdout)
        self.assertEqual({"count", "items", "unresolved"}, set(observed))
        self.assertEqual(observed["count"], len(observed["items"]))
        self.assertEqual("\n", first.stdout[-1:])
        ids = [item["shellId"] for item in observed["items"] if item["shellId"] is not None]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertTrue(all(len(item_id) == 64 for item_id in ids))
        self.assertEqual(observed["unresolved"], observed["count"] - len(ids))
        self.assertTrue(all(set(item) == {"viewIndex", "name", "x", "y", "shellId", "hasFileSystemPath"} for item in observed["items"]))
        self.assertTrue(all(isinstance(item["hasFileSystemPath"], bool) for item in observed["items"]))

        second = self.call("probe-shell-identities")
        self.assertEqual(0, second.returncode, second.stderr)
        self.assertEqual("", second.stderr)
        again = json.loads(second.stdout)
        self.assertEqual(sorted(ids), sorted(item["shellId"] for item in again["items"] if item["shellId"] is not None))

    def test_desktop_geometry_query(self):
        result = self.call("query-desktop-geometry")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stderr)
        geometry = json.loads(result.stdout)
        self.assertEqual({"gridSpacing", "usableArea", "monitors", "windowDpi", "monitorDpi", "autoArrange"}, set(geometry))
        self.assertEqual("\n", result.stdout[-1:])
        spacing = geometry["gridSpacing"]
        if spacing is not None:
            self.assertEqual({"x", "y"}, set(spacing))
            self.assertIsInstance(spacing["x"], int)
            self.assertIsInstance(spacing["y"], int)
            self.assertGreater(spacing["x"], 0)
            self.assertGreater(spacing["y"], 0)
        self.assertEqual({"left", "top", "right", "bottom"}, set(geometry["usableArea"]))
        self.assertIsInstance(geometry["monitors"], list)
        self.assertGreater(len(geometry["monitors"]), 0)
        for monitor in geometry["monitors"]:
            self.assertEqual({"bounds", "workArea", "primary"}, set(monitor))
            for area in (monitor["bounds"], monitor["workArea"]):
                self.assertEqual({"left", "top", "right", "bottom"}, set(area))
                self.assertLess(area["left"], area["right"])
                self.assertLess(area["top"], area["bottom"])
            self.assertIsInstance(monitor["primary"], bool)
        self.assertIn(geometry["autoArrange"], (True, False, None))
        self.assertTrue(geometry["windowDpi"] is None or geometry["windowDpi"] > 0)
        self.assertIsNone(geometry["monitorDpi"])

        invalid = self.call("query-desktop-geometry", "unexpected")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Usage: query-desktop-geometry\n", invalid.stderr)

        help_result = self.call("--help")
        self.assertEqual(0, help_result.returncode)
        self.assertEqual("", help_result.stderr)
        self.assertIn("query-desktop-geometry", help_result.stdout)

    def test_desktop_coordinate_probe(self):
        result = self.call("probe-desktop-coordinates")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stderr)
        observed = json.loads(result.stdout)
        self.assertEqual({"threadDpiAwareness", "windowDpiAwareness", "clientScreenRect", "monitors", "overlappingMonitorIndices"}, set(observed))
        self.assertEqual("\n", result.stdout[-1:])
        for key in ("threadDpiAwareness", "windowDpiAwareness"):
            self.assertIn(observed[key], ("unaware", "system", "perMonitor", None))
        if observed["clientScreenRect"] is not None:
            rect = observed["clientScreenRect"]
            self.assertEqual({"left", "top", "right", "bottom"}, set(rect))
            self.assertLess(rect["left"], rect["right"])
            self.assertLess(rect["top"], rect["bottom"])
        self.assertGreater(len(observed["monitors"]), 0)
        for monitor in observed["monitors"]:
            self.assertEqual({"bounds", "workArea", "primary"}, set(monitor))
        if observed["overlappingMonitorIndices"] is not None:
            self.assertTrue(all(isinstance(i, int) and 0 <= i < len(observed["monitors"]) for i in observed["overlappingMonitorIndices"]))

        invalid = self.call("probe-desktop-coordinates", "unexpected")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Usage: probe-desktop-coordinates\n", invalid.stderr)
        help_result = self.call("--help")
        self.assertEqual(0, help_result.returncode)
        self.assertEqual("", help_result.stderr)
        self.assertIn("probe-desktop-coordinates", help_result.stdout)

    def test_desktop_layout_diagnostic(self):
        result = self.call("diagnose-desktop-layout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stderr)
        self.assertEqual("\n", result.stdout[-1:])
        diagnostic = json.loads(result.stdout)
        self.assertEqual({"iconCount", "outsideClientAreaCount", "outsideClientAreaPositions", "note"}, set(diagnostic))
        self.assertEqual("Outside the reported client area does not mean an icon is misplaced.", diagnostic["note"])
        self.assertEqual(diagnostic["outsideClientAreaCount"], len(diagnostic["outsideClientAreaPositions"]))
        self.assertLessEqual(diagnostic["outsideClientAreaCount"], diagnostic["iconCount"])
        self.assertTrue(all(set(position) == {"x", "y"} for position in diagnostic["outsideClientAreaPositions"]))
        geometry = json.loads(self.call("query-desktop-geometry").stdout)
        icons = json.loads(self.call("list-icons").stdout)
        rect = geometry["usableArea"]
        expected = sorted((icon["x"], icon["y"]) for icon in icons if not (
            rect["left"] <= icon["x"] < rect["right"] and rect["top"] <= icon["y"] < rect["bottom"]
        ))
        self.assertEqual(len(icons), diagnostic["iconCount"])
        self.assertEqual(expected, sorted((position["x"], position["y"]) for position in diagnostic["outsideClientAreaPositions"]))

        invalid = self.call("diagnose-desktop-layout", "unexpected")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Usage: diagnose-desktop-layout\n", invalid.stderr)
        help_result = self.call("--help")
        self.assertEqual(0, help_result.returncode)
        self.assertEqual("", help_result.stderr)
        self.assertIn("diagnose-desktop-layout", help_result.stdout)

    def test_alphabetical_preview_uses_only_existing_positions(self):
        result = self.call("preview-icon-layout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stderr)
        self.assertEqual("\n", result.stdout[-1:])
        preview = json.loads(result.stdout)
        self.assertEqual({"snapshotId", "strategy", "iconCount", "changedCount", "proposals"}, set(preview))
        self.assertRegex(preview["snapshotId"], r"^[0-9a-f]{64}$")
        self.assertEqual("name-ascending", preview["strategy"])
        proposals = preview["proposals"]
        self.assertEqual(preview["iconCount"], len(proposals))
        self.assertEqual(preview["changedCount"], sum(p["changed"] for p in proposals))
        self.assertEqual(set(range(len(proposals))), {p["index"] for p in proposals})
        self.assertTrue(all(set(p) == {"index", "name", "fromX", "fromY", "toX", "toY", "changed"} for p in proposals))
        self.assertEqual(sorted((p["fromX"], p["fromY"]) for p in proposals),
                         sorted((p["toX"], p["toY"]) for p in proposals))
        self.assertEqual(sorted((p["toX"], p["toY"]) for p in proposals),
                         [(p["toX"], p["toY"]) for p in proposals])
        self.assertTrue(all(p["changed"] == ((p["fromX"], p["fromY"]) != (p["toX"], p["toY"])) for p in proposals))
        self.assertEqual(sorted(p["name"].lower() for p in proposals),
                         [p["name"].lower() for p in proposals])

        invalid = self.call("preview-icon-layout", "unexpected")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Usage: preview-icon-layout\n", invalid.stderr)
        help_result = self.call("--help")
        self.assertEqual(0, help_result.returncode)
        self.assertEqual("", help_result.stderr)
        self.assertIn("preview-icon-layout", help_result.stdout)

    def test_usage_category_preview_reuses_occupied_slots(self):
        result = self.call("preview-category-layout")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("", result.stderr)
        self.assertEqual("\n", result.stdout[-1:])
        preview = json.loads(result.stdout)
        self.assertEqual({"snapshotId", "strategy", "iconCount", "changedCount", "categories", "proposals"}, set(preview))
        self.assertRegex(preview["snapshotId"], r"^[0-9a-f]{64}$")
        self.assertEqual("usage-category", preview["strategy"])
        names = ["development", "communication", "media", "games", "utilities", "documents", "other"]
        self.assertEqual(names, [category["name"] for category in preview["categories"]])
        self.assertTrue(all(set(category) == {"name", "count"} for category in preview["categories"]))
        proposals = preview["proposals"]
        self.assertEqual(preview["iconCount"], len(proposals))
        self.assertEqual(preview["iconCount"], sum(category["count"] for category in preview["categories"]))
        self.assertEqual(preview["changedCount"], sum(p["changed"] for p in proposals))
        self.assertEqual(set(range(len(proposals))), {p["index"] for p in proposals})
        self.assertTrue(all(set(p) == {"index", "name", "category", "fromX", "fromY", "toX", "toY", "changed"} for p in proposals))
        self.assertEqual(sorted((p["fromX"], p["fromY"]) for p in proposals),
                         sorted((p["toX"], p["toY"]) for p in proposals))
        self.assertEqual(sorted((p["toX"], p["toY"]) for p in proposals),
                         [(p["toX"], p["toY"]) for p in proposals])
        self.assertTrue(all(p["changed"] == ((p["fromX"], p["fromY"]) != (p["toX"], p["toY"])) for p in proposals))
        self.assertEqual(sorted(proposals, key=lambda p: (names.index(p["category"]), p["name"].lower())), proposals)
        self.assertEqual({name: sum(p["category"] == name for p in proposals) for name in names},
                         {category["name"]: category["count"] for category in preview["categories"]})

        invalid = self.call("preview-category-layout", "unexpected")
        self.assertEqual(1, invalid.returncode)
        self.assertEqual("", invalid.stdout)
        self.assertEqual("ERROR: Usage: preview-category-layout\n", invalid.stderr)
        help_result = self.call("--help")
        self.assertEqual(0, help_result.returncode)
        self.assertEqual("", help_result.stderr)
        self.assertIn("preview-category-layout", help_result.stdout)

    def test_shell_move_rejects_invalid_input_without_moving(self):
        before = self.call("capture-snapshot")
        self.assertEqual(0, before.returncode, before.stderr)
        snapshot = json.loads(before.stdout)["snapshotId"]
        for arguments in (("move-shell-icon",), ("move-shell-icon", "invalid", "0", "10", "20"),
                          ("move-shell-icon", snapshot, "not-an-index", "10", "20")):
            rejected = self.call(*arguments)
            self.assertEqual(1, rejected.returncode)
            self.assertEqual("", rejected.stdout)
            self.assertEqual("ERROR: Usage: move-shell-icon <snapshotId> <index> <X> <Y>\n", rejected.stderr)
        after = self.call("capture-snapshot")
        self.assertEqual(0, after.returncode, after.stderr)
        self.assertEqual(snapshot, json.loads(after.stdout)["snapshotId"])
        help_result = self.call("--help")
        self.assertEqual(0, help_result.returncode)
        self.assertIn("move-shell-icon", help_result.stdout)

    def test_existing_list_command_is_unchanged(self):
        listed = self.call("listar-iconos")
        self.assertEqual(0, listed.returncode, listed.stderr)
        self.assertEqual("", listed.stderr)
        icons = json.loads(listed.stdout)
        self.assertIsInstance(icons, list)
        self.assertTrue(all(set(icon) == {"indice", "nombre", "x", "y"} for icon in icons))
        self.assertEqual("\n", listed.stdout[-1:])


if __name__ == "__main__":
    unittest.main()
