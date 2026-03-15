"""Tests for MCP tools and definitions."""

import pytest

from ida_mcp.mcp_server import TOOLS_LIST
from ida_mcp.tools.definitions import TOOL_DESCRIPTIONS, INPUT_SCHEMA


def test_tools_count() -> None:
    assert len(TOOLS_LIST) == 76


def test_definitions_match_tools() -> None:
    names = {t.name for t in TOOLS_LIST}
    assert names == set(TOOL_DESCRIPTIONS), "TOOLS_LIST and TOOL_DESCRIPTIONS must have same names"


def test_each_tool_has_schema() -> None:
    for t in TOOLS_LIST:
        assert t.name
        assert t.description
        assert t.inputSchema == INPUT_SCHEMA
        assert t.inputSchema.get("type") == "object"


def test_input_schema_accepts_additional_properties() -> None:
    assert INPUT_SCHEMA.get("additionalProperties") is True
