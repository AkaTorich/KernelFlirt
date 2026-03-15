"""Tests for ida_client."""

from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from ida_mcp.ida_client import IdaClient, IdaConnectionError


def test_ida_client_init() -> None:
    c = IdaClient(host="127.0.0.1", port=13370)
    assert c.base_url == "http://127.0.0.1:13370"
    c2 = IdaClient(host="localhost", port=9999)
    assert c2.base_url == "http://localhost:9999"


def test_ida_connection_error() -> None:
    assert issubclass(IdaConnectionError, Exception)


@pytest.mark.asyncio
async def test_call_success() -> None:
    response = MagicMock()
    response.status_code = 200
    response.json.return_value = {"jsonrpc": "2.0", "result": {"filename": "test.exe"}, "id": 1}
    response.raise_for_status = lambda: None

    mock_client = MagicMock()
    mock_client.post = AsyncMock(return_value=response)
    mock_client.is_closed = False
    mock_client.aclose = AsyncMock()

    with patch.object(IdaClient, "_get_client", new_callable=AsyncMock, return_value=mock_client):
        client = IdaClient()
        result = await client.call("get_database_info")
        assert result == {"filename": "test.exe"}


@pytest.mark.asyncio
async def test_call_ida_error() -> None:
    response = MagicMock()
    response.status_code = 200
    response.json.return_value = {
        "jsonrpc": "2.0",
        "error": {"code": -32000, "message": "No function at 0x0"},
        "id": 1,
    }
    response.raise_for_status = lambda: None

    mock_client = MagicMock()
    mock_client.post = AsyncMock(return_value=response)
    mock_client.is_closed = False

    with patch.object(IdaClient, "_get_client", new_callable=AsyncMock, return_value=mock_client):
        client = IdaClient()
        with pytest.raises(IdaConnectionError, match="IDA error: No function at 0x0"):
            await client.call("get_function_info", address="0x0")
