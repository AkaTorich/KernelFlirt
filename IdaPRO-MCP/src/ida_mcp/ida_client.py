"""
HTTP JSON-RPC client for IDA Pro MCP plugin.

Connects to the plugin at http://ida_host:ida_port and forwards
method calls. Uses httpx async client with retry and timeout.
"""

from __future__ import annotations

import json
import logging
from typing import Any

import httpx

logger = logging.getLogger(__name__)

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 13370
DEFAULT_TIMEOUT = 30.0
DECOMPILE_TIMEOUT = 120.0

# Methods that may take long (decompile, analysis)
LONG_TIMEOUT_METHODS = frozenset({
    "decompile_function", "decompile_address",
    "get_local_variables", "rename_local_variable", "set_local_variable_type",
    "get_decompiler_comments",
    "get_functions", "get_segments", "get_strings", "get_names",
    "get_call_graph", "get_function_flowchart",
})


class IdaConnectionError(Exception):
    """IDA plugin unreachable or returned error."""
    pass


class IdaClient:
    """Async HTTP client for IDA Pro MCP plugin JSON-RPC."""

    def __init__(
        self,
        host: str = DEFAULT_HOST,
        port: int = DEFAULT_PORT,
        timeout: float = DEFAULT_TIMEOUT,
        long_timeout: float = DECOMPILE_TIMEOUT,
    ):
        self.base_url = f"http://{host}:{port}"
        self.timeout = timeout
        self.long_timeout = long_timeout
        self._client: httpx.AsyncClient | None = None

    async def _get_client(self) -> httpx.AsyncClient:
        if self._client is None or self._client.is_closed:
            self._client = httpx.AsyncClient(
                base_url=self.base_url,
                timeout=httpx.Timeout(self.timeout),
            )
        return self._client

    async def close(self) -> None:
        if self._client and not self._client.is_closed:
            await self._client.aclose()
            self._client = None

    async def __aenter__(self) -> "IdaClient":
        await self._get_client()
        return self

    async def __aexit__(self, *args: Any) -> None:
        await self.close()

    async def health(self) -> dict[str, Any]:
        """GET /health — check plugin is up and get registered methods."""
        client = await self._get_client()
        try:
            r = await client.get("/health", timeout=5.0)
            r.raise_for_status()
            return r.json()
        except httpx.HTTPError as e:
            raise IdaConnectionError(f"IDA health check failed: {e}") from e

    async def call(self, method: str, **params: Any) -> Any:
        """
        Call IDA JSON-RPC method. Params passed as JSON-RPC params object.
        Returns result or raises IdaConnectionError / ValueError from plugin.
        """
        client = await self._get_client()
        timeout = self.long_timeout if method in LONG_TIMEOUT_METHODS else self.timeout
        payload = {
            "jsonrpc": "2.0",
            "method": method,
            "params": params,
            "id": 1,
        }
        try:
            r = await client.post(
                "/",
                content=json.dumps(payload),
                headers={"Content-Type": "application/json"},
                timeout=timeout,
            )
            r.raise_for_status()
            data = r.json()
        except httpx.HTTPError as e:
            raise IdaConnectionError(f"IDA request failed: {e}") from e
        except json.JSONDecodeError as e:
            raise IdaConnectionError(f"IDA invalid JSON: {e}") from e

        if "error" in data:
            err = data["error"]
            msg = err.get("message", str(err))
            raise IdaConnectionError(f"IDA error: {msg}")

        return data.get("result")
