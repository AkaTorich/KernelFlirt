# Tool definitions — merged from category modules.
# Params passed through to IDA plugin; addresses as hex strings (e.g. "0x401000").

from ida_mcp.tools.analysis import DESCRIPTIONS as _analysis
from ida_mcp.tools.bytes import DESCRIPTIONS as _bytes
from ida_mcp.tools.comments import DESCRIPTIONS as _comments
from ida_mcp.tools.decompilation import DESCRIPTIONS as _decompilation
from ida_mcp.tools.debugger import DESCRIPTIONS as _debugger
from ida_mcp.tools.disassembly import DESCRIPTIONS as _disassembly
from ida_mcp.tools.functions import DESCRIPTIONS as _functions
from ida_mcp.tools.graph import DESCRIPTIONS as _graph
from ida_mcp.tools.naming import DESCRIPTIONS as _naming
from ida_mcp.tools.search import DESCRIPTIONS as _search
from ida_mcp.tools.scripting import DESCRIPTIONS as _scripting
from ida_mcp.tools.segments import DESCRIPTIONS as _segments
from ida_mcp.tools.types import DESCRIPTIONS as _types
from ida_mcp.tools.xrefs import DESCRIPTIONS as _xrefs

TOOL_DESCRIPTIONS = {
    **_analysis,
    **_disassembly,
    **_decompilation,
    **_functions,
    **_xrefs,
    **_naming,
    **_types,
    **_segments,
    **_bytes,
    **_search,
    **_comments,
    **_debugger,
    **_graph,
    **_scripting,
}

INPUT_SCHEMA = {
    "type": "object",
    "properties": {},
    "additionalProperties": True,
}
