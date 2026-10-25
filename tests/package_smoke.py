"""Validate an extracted self-contained release. --desktop adds strictly read-only live calls."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import queue
import subprocess
import tempfile
import threading
import time
import zipfile


def check(value, message):
    if not value:
        raise RuntimeError(message)


def check_archive_path(root, entry):
    # ZIP member names use POSIX separators, but also reject Windows-rooted paths
    # and backslash traversal before resolving the target on the host platform.
    posix_entry = PurePosixPath(entry.replace("\\", "/"))
    windows_entry = PureWindowsPath(entry)
    check(bool(posix_entry.parts) and not posix_entry.is_absolute() and ".." not in posix_entry.parts
          and not windows_entry.is_absolute() and not windows_entry.drive,
          f"Unsafe archive path: {entry!r}")
    resolved_root = root.resolve()
    resolved_target = resolved_root.joinpath(*posix_entry.parts).resolve()
    try:
        resolved_target.relative_to(resolved_root)
    except ValueError as error:
        raise RuntimeError(f"Unsafe archive path: {entry!r}") from error


class Client:
    def __init__(self, executable, cwd):
        env = os.environ.copy()
        # A self-contained package must not depend on dotnet or an installed runtime.
        system_root = env.get("SystemRoot") or env.get("WINDIR") or r"C:\Windows"
        env["SystemRoot"] = system_root
        env["PATH"] = os.path.join(system_root, "System32") + os.pathsep + system_root
        env["DOTNET_ROOT"] = str(cwd / "no-dotnet-runtime")
        env["DOTNET_ROOT_X64"] = env["DOTNET_ROOT"]
        env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        self.process = subprocess.Popen(
            [str(executable)], cwd=cwd, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.responses = queue.Queue()
        self.counter = 0
        threading.Thread(target=self._read, args=(self.process.stdout,), daemon=True).start()
        # Drain logs so a pipe cannot block the server. No desktop names are printed by this test.
        threading.Thread(target=self._drain, daemon=True).start()

    def _drain(self):
        for _ in self.process.stderr:
            pass

    def _read(self, output):
        try:
            for line in output:
                self.responses.put(json.loads(line))
        except Exception as error:
            self.responses.put({"reader_error": str(error)})

    def send(self, message):
        self.process.stdin.write(json.dumps(message, ensure_ascii=True) + "\n")
        self.process.stdin.flush()

    def call(self, method, params=None):
        self.counter += 1
        identity = self.counter
        self.send({"jsonrpc": "2.0", "id": identity, "method": method, "params": params or {}})
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            try:
                response = self.responses.get(timeout=0.25)
            except queue.Empty:
                check(self.process.poll() is None, "Package server exited during an MCP call")
                continue
            check("reader_error" not in response, "Server stdout was not valid MCP JSON")
            if response.get("id") != identity:
                continue
            check("error" not in response, f"MCP request failed: {response.get('error')}")
            return response["result"]
        raise RuntimeError("Package MCP request timed out")

    def tool(self, name, arguments=None):
        result = self.call("tools/call", {"name": name, "arguments": arguments or {}})
        check(not result.get("isError"), f"Read-only tool failed: {name}: {result.get('content')}")
        return result["structuredContent"]

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        self.process.stdout.close()
        self.process.stderr.close()


def positions(state):
    return sorted((icon["name"], icon["position"]["x"], icon["position"]["y"]) for icon in state["icons"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--desktop", action="store_true", help="Read real desktop state and preview; never apply")
    args = parser.parse_args()
    check(os.name == "nt", "Run this Windows package smoke check on Windows")
    archive = args.archive.resolve()
    checksum = Path(str(archive) + ".sha256")
    if checksum.exists():
        check(hashlib.sha256(archive.read_bytes()).hexdigest() == checksum.read_text().split()[0], "SHA256 mismatch")
    with tempfile.TemporaryDirectory(prefix="desk-organizer-package-") as directory:
        staging = Path(directory)
        with zipfile.ZipFile(archive) as zipped:
            for entry in zipped.namelist():
                check_archive_path(staging, entry)
            zipped.extractall(staging)
        roots = list(staging.glob("*/package.json"))
        check(len(roots) == 1, "Package must contain exactly one root manifest")
        root = roots[0].parent
        manifest = json.loads(roots[0].read_text(encoding="utf-8-sig"))
        check(manifest["runtime"] == "win-x64" and manifest["selfContained"], "Unexpected runtime metadata")
        for file in [manifest["server"], manifest["skill"], "README.md", "examples/mcp.config.json",
                     "skills/desk-organizer/references/mcp-tools.md", "server/coreclr.dll"]:
            check((root / file).is_file(), f"Missing package file: {file}")
        for example, expected_args in [("mcp.config.json", []), ("mcp.experimental-write.config.json", ["--enable-writes"])]:
            config = json.loads((root / "examples" / example).read_text())["mcpServers"]["desk-organizer"]
            check(PureWindowsPath(config["command"]).parts[-2:] == ("server", "DeskOrganizer.Mcp.exe"),
                  "Configuration example does not target the packaged server")
            check(config["args"] == expected_args, "Incorrect sample write opt-in")
        client = Client(root / manifest["server"], staging)
        try:
            initialized = client.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                "clientInfo": {"name": "desk-organizer-package-check", "version": "1.0"}})
            check("serverInfo" in initialized, "Missing MCP server metadata")
            client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
            tools = client.call("tools/list")["tools"]
            check({tool["name"] for tool in tools} == {"get_desktop_state", "preview_layout"},
                  "Read-only server unexpectedly exposed operation tools")
            check(all(tool.get("annotations", {}).get("readOnlyHint") is True for tool in tools),
                  "Incorrect read-only tool annotations")
            preview_tool = next(tool for tool in tools if tool["name"] == "preview_layout")
            check("spatial-groups" in json.dumps(preview_tool["inputSchema"]), "Missing spatial strategy schema")
            print("PASS extracted self-contained package, MCP initialization/discovery, read-only registration, skill files")
            if args.desktop:
                before = client.tool("get_desktop_state")
                check(not before["capabilities"]["canApply"], "Default server enabled writing")
                icons = before["icons"]
                check(len(icons) >= 2, "Live spatial preview needs at least two icons")
                count = min(8, len(icons))
                chunk, remainder = divmod(len(icons), count)
                groups, offset = [], 0
                for index in range(count):
                    length = chunk + (index < remainder)
                    groups.append({"name": f"Group {index + 1}",
                        "iconRefs": [icon["iconRef"] for icon in icons[offset:offset + length]]})
                    offset += length
                plan = client.tool("preview_layout", {"snapshotId": before["snapshotId"],
                    "strategy": "spatial-groups", "groups": groups})
                check(plan["previewOnly"] and plan["iconCount"] == len(icons), "Invalid read-only preview")
                targets = {(item["to"]["x"], item["to"]["y"]) for item in plan["proposals"]}
                check(len(targets) == len(icons), "Preview collided destinations")
                after = client.tool("get_desktop_state")
                check(positions(before) == positions(after), "Desktop changed during read-only preview")
                print(f"PASS live read-only spatial preview: {len(icons)} icons, unique destinations, positions unchanged")
        finally:
            client.close()


if __name__ == "__main__":
    main()
