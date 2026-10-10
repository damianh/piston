"""Exercise an installed Piston tool against a disposable one-test solution."""

import json
import pathlib
import re
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def rpc(url, method, params, request_id):
    request = urllib.request.Request(
        url,
        data=json.dumps(
            {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params}
        ).encode(),
        headers={
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
            "MCP-Protocol-Version": "2025-06-18",
        },
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        text = response.read().decode()
        if response.headers.get_content_type() == "text/event-stream":
            text = "\n".join(line[6:] for line in text.splitlines() if line.startswith("data: "))
        result = json.loads(text)
    if "error" in result:
        raise RuntimeError(result["error"])
    return result["result"]


def main():
    tool = str(pathlib.Path(sys.argv[1]).resolve())
    subprocess.run([tool, "--help"], check=True)
    with tempfile.TemporaryDirectory(prefix="piston-tool-smoke-") as directory:
        root = pathlib.Path(directory)
        (root / "Smoke.csproj").write_text(
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
  </ItemGroup>
</Project>
"""
        )
        (root / "SmokeTests.cs").write_text(
            "public class SmokeTests { [Xunit.Fact] public void Passes() { Xunit.Assert.True(true); } }\n"
        )
        (root / "Smoke.slnx").write_text('<Solution><Project Path="Smoke.csproj" /></Solution>\n')
        solution = str(root / "Smoke.slnx")
        web_port, mcp_port = free_port(), free_port()
        while mcp_port == web_port:
            mcp_port = free_port()
        pipe = f"piston-smoke-{uuid.uuid4().hex}"
        web_url = f"http://localhost:{web_port}"
        mcp_url = f"http://localhost:{mcp_port}"
        with (root / "daemon.log").open("w+") as log:
            daemon = subprocess.Popen(
                [tool, "daemon", solution, "--web-port", str(web_port),
                 "--mcp-port", str(mcp_port), "--pipe-name", pipe, "--coverage"],
                cwd=root, stdout=log, stderr=log,
            )
            try:
                deadline = time.monotonic() + 120
                while True:
                    if daemon.poll() is not None:
                        raise RuntimeError(f"Daemon exited with {daemon.returncode}")
                    try:
                        with urllib.request.urlopen(web_url, timeout=2) as response:
                            page = response.read().decode()
                        break
                    except (urllib.error.URLError, TimeoutError):
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Installed tool did not start its web server")
                        time.sleep(0.25)
                assert "_framework/" in page, page
                framework = re.search(r'src="(_framework/blazor\.webassembly[^"]*\.js)"', page)
                assert framework is not None, page
                with urllib.request.urlopen(f"{web_url}/{framework[1]}", timeout=10) as response:
                    assert response.status == 200
                    assert len(response.read()) > 1000
                    assert "javascript" in response.headers.get("Content-Type", "")

                rpc(mcp_url, "initialize", {
                    "protocolVersion": "2025-06-18", "capabilities": {},
                    "clientInfo": {"name": "piston-package-smoke", "version": "1.0"},
                }, 1)
                tools = rpc(mcp_url, "tools/list", {}, 2)["tools"]
                names = {tool["name"] for tool in tools}
                assert names == {"run_tests", "get_test_results", "set_test_filter", "clear_results"}, names
                result = rpc(mcp_url, "tools/call", {"name": "run_tests", "arguments": {}}, 3)
                assert not result.get("isError", False), result
                text = "\n".join(item.get("text", "") for item in result["content"])
                assert "Passed: 1, Failed: 0" in text, text
                assert "Phase: Watching" in text, text
                database = root / ".piston" / "piston.db"
                with database.open("rb") as stream:
                    assert stream.read(16) == b"SQLite format 3\x00"
                status = subprocess.run(
                    [tool, "status", solution, "--pipe-name", pipe],
                    check=True, capture_output=True, text=True, timeout=15,
                )
                assert "Daemon running." in status.stdout, status.stdout
                print("PASS: installed CLI, bundled web/framework assets, HTTP MCP test-only surface, "
                      "one passing test, native SQLite coverage store and named-pipe status")
            except BaseException:
                log.flush()
                log.seek(0)
                print(log.read(), file=sys.stderr)
                diagnostics = root / ".piston" / "diagnostics.log"
                if diagnostics.exists():
                    print(diagnostics.read_text(), file=sys.stderr)
                raise
            finally:
                if daemon.poll() is None:
                    daemon.send_signal(signal.SIGINT)
                    try:
                        daemon.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        daemon.kill()
                        daemon.wait(timeout=10)


if __name__ == "__main__":
    main()
