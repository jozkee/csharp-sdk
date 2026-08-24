# /// script
# requires-python = ">=3.10"
# dependencies = [
#   "mcp==1.27.2",
#   "uvicorn==0.48.0",
# ]
# ///

"""Windows command-resolution reference tests for the Python MCP SDK.

Run with:
    uv run tests/Interop/WindowsCommandResolution/windows_command_resolution_test.py
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.os.win32.utilities import get_windows_executable_command


@unittest.skipUnless(sys.platform == "win32", "Windows-only command-resolution tests")
class WindowsCommandResolutionTests(unittest.IsolatedAsyncioTestCase):
    async def test_python_mcp_sdk_launches_real_npx_server_memory(self) -> None:
        """Mirrors Command="npx", Arguments=["-y", server-memory] end to end."""
        with tempfile.TemporaryDirectory(prefix="mcp-python-memory-") as temp_directory:
            memory_path = Path(temp_directory) / "memory.jsonl"
            server = StdioServerParameters(
                command="npx",
                args=["-y", "@modelcontextprotocol/server-memory"],
                env={"MEMORY_FILE_PATH": str(memory_path)},
            )

            async with stdio_client(server) as (read_stream, write_stream):
                async with ClientSession(read_stream, write_stream) as session:
                    await session.initialize()

                    tools = await session.list_tools()
                    self.assertIn("read_graph", {tool.name for tool in tools.tools})

                    result = await session.call_tool("read_graph", {})
                    self.assertFalse(result.isError)
                    self.assertEqual(
                        result.structuredContent,
                        {"entities": [], "relations": []},
                    )

    async def _assert_marker_round_trip(
        self,
        command: str,
        expected_marker: str,
        *,
        cwd: Path | None = None,
        env: dict[str, str] | None = None,
    ) -> None:
        server_env = {"PATHEXT": ".COM;.EXE;.BAT;.CMD"}
        server_env.update(env or {})
        server = StdioServerParameters(
            command=command,
            env=server_env,
            cwd=cwd,
        )

        async with stdio_client(server) as (read_stream, write_stream):
            async with ClientSession(read_stream, write_stream) as session:
                await session.initialize()

                tools = await session.list_tools()
                self.assertIn("launcher-marker", {tool.name for tool in tools.tools})

                result = await session.call_tool("launcher-marker", {})
                self.assertFalse(result.isError)
                self.assertEqual(result.content[0].text, expected_marker)

    async def test_python_mcp_sdk_appends_pathext_to_rooted_extensionless_command(self) -> None:
        """A rooted command fixes the directory but still resolves its .cmd extension."""
        with tempfile.TemporaryDirectory(prefix="mcp-python-rooted-") as temp_directory:
            root = Path(temp_directory)
            rooted_command = root / "rooted-npx"
            self.assertTrue(rooted_command.is_absolute(), f"command is not rooted: {rooted_command}")
            shim = rooted_command.with_suffix(".cmd")
            node = shutil.which("node.exe")
            self.assertIsNotNone(node, "node.exe must be available on PATH")
            server_path = Path(__file__).with_name("mcp-server.mjs")
            shim.write_text(
                f'@echo off\r\n"{node}" "{server_path}" "rooted-npx.cmd"\r\n',
                encoding="ascii",
            )

            await self._assert_marker_round_trip(str(rooted_command), "rooted-npx.cmd")

    async def test_python_mcp_sdk_appends_pathext_to_rooted_command_with_extension(self) -> None:
        """A rooted name ending in .exe can still resolve to an .exe.cmd shim."""
        with tempfile.TemporaryDirectory(prefix="mcp-python-rooted-") as temp_directory:
            root = Path(temp_directory)
            rooted_command = root / "rooted-uvicorn.exe"
            self.assertTrue(rooted_command.is_absolute(), f"command is not rooted: {rooted_command}")
            shim = Path(f"{rooted_command}.cmd")
            node = shutil.which("node.exe")
            self.assertIsNotNone(node, "node.exe must be available on PATH")
            server_path = Path(__file__).with_name("mcp-server.mjs")
            shim.write_text(
                f'@echo off\r\n"{node}" "{server_path}" "rooted-uvicorn.exe.cmd"\r\n',
                encoding="ascii",
            )

            await self._assert_marker_round_trip(str(rooted_command), "rooted-uvicorn.exe.cmd")

    async def test_python_mcp_sdk_checks_process_cwd_then_launches_relative_directory_command_from_server_cwd(
        self,
    ) -> None:
        """Resolution checks the process cwd; spawning then applies server.cwd, without PATH."""
        with tempfile.TemporaryDirectory(prefix="mcp-python-relative-") as temp_directory:
            root = Path(temp_directory)
            process_cwd = root / "process cwd"
            server_cwd = root / "server cwd"
            path_shims = root / "path shims"
            relative_command = Path("relative-tools") / "launcher"
            self.assertFalse(relative_command.is_absolute(), f"command is not relative: {relative_command}")
            self.assertNotEqual(relative_command.parent, Path("."), "command lacks a directory component")

            process_cwd_command = process_cwd / relative_command.with_suffix(".cmd")
            server_cwd_command = server_cwd / relative_command.with_suffix(".cmd")
            path_command = path_shims / relative_command.with_suffix(".cmd")
            process_cwd_command.parent.mkdir(parents=True)
            server_cwd_command.parent.mkdir(parents=True)
            path_command.parent.mkdir(parents=True)

            node = shutil.which("node.exe")
            self.assertIsNotNone(node, "node.exe must be available on PATH")
            server_path = Path(__file__).with_name("mcp-server.mjs")
            process_cwd_command.write_text(
                f'@echo off\r\n"{node}" "{server_path}" "relative-process-cwd.cmd"\r\n',
                encoding="ascii",
            )
            server_cwd_command.write_text(
                f'@echo off\r\n"{node}" "{server_path}" "relative-server-cwd.cmd"\r\n',
                encoding="ascii",
            )
            path_command.write_text(
                f'@echo off\r\n"{node}" "{server_path}" "relative-path.cmd"\r\n',
                encoding="ascii",
            )

            path = f"{path_shims}{os.pathsep}{os.environ.get('PATH', '')}"
            original_cwd = Path.cwd()
            try:
                os.chdir(process_cwd)
                resolved_command = Path(get_windows_executable_command(str(relative_command)))
                self.assertFalse(resolved_command.is_absolute())
                self.assertEqual(resolved_command.suffix.lower(), ".cmd")
                await self._assert_marker_round_trip(
                    str(relative_command),
                    "relative-server-cwd.cmd",
                    cwd=server_cwd,
                    env={"PATH": path},
                )
            finally:
                os.chdir(original_cwd)

    def test_python_mcp_sdk_resolves_and_launches_real_uvicorn_executable(self) -> None:
        """Verifies the resolver used by StdioServerParameters finds uvicorn.exe."""
        resolved = Path(get_windows_executable_command("uvicorn"))

        self.assertTrue(resolved.is_file(), f"uvicorn did not resolve to a file: {resolved}")
        self.assertEqual(resolved.suffix.lower(), ".exe")

        completed = subprocess.run(
            [str(resolved), "--version"],
            check=False,
            capture_output=True,
            text=True,
            timeout=30,
            env=os.environ.copy(),
        )

        self.assertEqual(
            completed.returncode,
            0,
            f"uvicorn failed to launch: stdout={completed.stdout!r}, stderr={completed.stderr!r}",
        )
        self.assertIn("uvicorn", completed.stdout.lower())


if __name__ == "__main__":
    unittest.main(verbosity=2)
