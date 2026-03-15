"""
MCP Server for IDA Pro — exposes IDA plugin methods as MCP tools.

Runs as standalone process (stdio or SSE). Proxies tool calls to IDA plugin
via HTTP JSON-RPC. Requires IDA Pro with ida_mcp_plugin loaded.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import sys
from datetime import datetime
from typing import Any

# Log to file when run as subprocess (Cursor doesn't show stderr)
_LOG_FILE = os.environ.get("IDA_MCP_LOG") or os.path.join(os.environ.get("TEMP", "/tmp"), "ida_mcp_server.log")


def _log(msg: str) -> None:
    try:
        with open(_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(f"{datetime.now().isoformat()} {msg}\n")
    except Exception:
        pass


_log("mcp_server module loading")

import anyio
import click
import mcp.types as types
from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.server.lowlevel import NotificationOptions

from ida_mcp.ida_client import IdaClient, IdaConnectionError
from ida_mcp.tools.definitions import TOOL_DESCRIPTIONS, INPUT_SCHEMA

logger = logging.getLogger(__name__)

# Build tool list for list_tools
TOOLS_LIST: list[types.Tool] = [
    types.Tool(
        name=name,
        description=desc,
        inputSchema=INPUT_SCHEMA,
    )
    for name, desc in TOOL_DESCRIPTIONS.items()
]
_log("TOOLS_LIST built")

_ida_client_ctx: dict[str, IdaClient | None] = {"client": None}

server = Server(
    name="ida-mcp",
    version=None,
    instructions="Tools to interact with IDA Pro: disassembly, decompilation, functions, xrefs, naming, types, segments, bytes, search, comments, debugger, navigation, scripting. Addresses are hex strings (e.g. 0x401000). Ensure IDA is running with the IDA MCP plugin loaded on port 13370.",
)
_log("server created")

@server.list_tools()
async def list_tools() -> list[types.Tool]:
    return TOOLS_LIST


def _wrap_result(result: Any) -> list[types.ContentBlock]:
    """Wrap IDA plugin result (dict/list) as MCP TextContent."""
    if result is None:
        return [types.TextContent(type="text", text="OK")]
    return [types.TextContent(type="text", text=json.dumps(result, indent=2, default=str))]


@server.call_tool(validate_input=False)
async def call_tool(name: str, arguments: dict[str, Any]) -> dict[str, Any] | list[types.ContentBlock]:
    client = _ida_client_ctx.get("client")
    if client is None:
        return [types.TextContent(type="text", text="Error: IDA client not initialized")]
    try:
        result = await client.call(name, **arguments)
        if result is None:
            return [types.TextContent(type="text", text="OK")]
        return _wrap_result(result)
    except IdaConnectionError as e:
        return [types.TextContent(type="text", text=f"IDA connection error: {e}")]
    except Exception as e:
        logger.exception("Tool %s failed", name)
        return [types.TextContent(type="text", text=f"Error: {e}")]


_log("handlers registered")

async def run_stdio_async(ida_host: str, ida_port: int) -> None:
    _log("run_stdio_async starting")
    client = IdaClient(host=ida_host, port=ida_port)
    _ida_client_ctx["client"] = client
    try:
        init_options = server.create_initialization_options(NotificationOptions())
        _log("entering stdio_server context")
        async with stdio_server() as (read_stream, write_stream):
            _log("calling server.run")
            await server.run(read_stream, write_stream, init_options)
            _log("server.run returned")
    except Exception as e:
        _log(f"run_stdio_async error: {e}")
        import traceback
        _log(traceback.format_exc())
        raise
    finally:
        await client.close()
        _ida_client_ctx["client"] = None
        _log("run_stdio_async done")


def main_anyio(ida_host: str, ida_port: int) -> None:
    _log("main_anyio starting")
    try:
        anyio.run(run_stdio_async, ida_host, ida_port)
        _log("main_anyio finished normally")
    except Exception as e:
        _log(f"main_anyio error: {e}")
        import traceback
        _log(traceback.format_exc())
        raise


@click.command()
@click.option(
    "--transport",
    type=click.Choice(["stdio"]),
    default="stdio",
    help="Transport: stdio (SSE not implemented yet)",
)
@click.option("--port", default=3000, help="Port for SSE (future use)")
@click.option("--ida-host", default="127.0.0.1", help="IDA plugin host")
@click.option("--ida-port", default=13370, type=int, help="IDA plugin port")
def main(transport: str, port: int, ida_host: str, ida_port: int) -> None:
    _log("main() entered")
    logging.basicConfig(
        level=logging.WARNING,
        format="%(message)s",
        stream=sys.stderr,
    )
    logger.setLevel(logging.INFO)
    if transport == "stdio":
        try:
            main_anyio(ida_host, ida_port)
        except Exception as e:
            import traceback
            _log(f"main exception: {e}\n{traceback.format_exc()}")
            print(f"ida-mcp-server error: {e}", file=sys.stderr)
            traceback.print_exc(file=sys.stderr)
            sys.exit(1)
    else:
        click.echo("Only stdio transport is supported.", err=True)
        sys.exit(1)


if __name__ == "__main__":
    main()