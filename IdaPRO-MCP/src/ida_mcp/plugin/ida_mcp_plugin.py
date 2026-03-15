"""
IDA Pro MCP Plugin — HTTP JSON-RPC server exposing IDA Pro APIs.

Loads inside IDA Pro 9 and serves JSON-RPC 2.0 requests on localhost:13370.
All IDA API calls are marshalled to the main thread via execute_sync.
"""

import json
import threading
import traceback
from http.server import HTTPServer, BaseHTTPRequestHandler
from functools import wraps

import ida_idaapi
import ida_ida
import ida_idp
import ida_kernwin
import ida_funcs
import ida_name
import ida_bytes
import ida_segment
import ida_nalt
import ida_typeinf
import ida_gdl
import ida_search
import ida_dbg
import ida_hexrays
import ida_lines
import ida_ua
import ida_auto
import idc
import idautils

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 13370

# ---------------------------------------------------------------------------
# Thread-safe execution
# ---------------------------------------------------------------------------

def execute_on_main_thread(func, *args, **kwargs):
    """Marshal a call onto IDA's main thread via execute_sync."""
    result = {}

    def wrapper():
        try:
            result["value"] = func(*args, **kwargs)
        except Exception as e:
            result["error"] = traceback.format_exc()
        return 0

    ida_kernwin.execute_sync(wrapper, ida_kernwin.MFF_WRITE)
    if "error" in result:
        raise RuntimeError(result["error"])
    return result.get("value")


# ---------------------------------------------------------------------------
# API method registry
# ---------------------------------------------------------------------------

_api_methods = {}


def api_method(name):
    """Decorator to register a JSON-RPC method."""
    def decorator(func):
        @wraps(func)
        def safe_wrapper(**params):
            return execute_on_main_thread(func, **params)
        _api_methods[name] = safe_wrapper
        return func
    return decorator


# ---------------------------------------------------------------------------
# Address helpers
# ---------------------------------------------------------------------------

def parse_addr(value):
    """Parse an address from string or int."""
    if isinstance(value, str):
        return int(value, 16) if value.startswith("0x") else int(value)
    return int(value)


def fmt_addr(ea):
    """Format address as hex string."""
    return hex(ea)


# ---------------------------------------------------------------------------
# API Methods — Analysis & Info
# ---------------------------------------------------------------------------

@api_method("get_database_info")
def _get_database_info(**params):
    # IDA 9: get_inf_structure() removed; use ida_ida accessors
    try:
        procname = ida_ida.getinf_str(ida_ida.INF_STR_PROCNAME)
    except AttributeError:
        procname = ida_idp.get_idp_name() or ""
    bitness = 16 if ida_ida.inf_is_16bit() else (64 if ida_ida.inf_is_64bit() else 32)
    entries = []
    for i, ordinal, ea, name in idautils.Entries():
        entries.append({"index": i, "ordinal": ordinal, "address": fmt_addr(ea), "name": name or ""})
    try:
        file_type = ida_nalt.get_file_type_name()
    except AttributeError:
        file_type = ""
    return {
        "filename": ida_nalt.get_root_filename(),
        "filepath": ida_nalt.get_input_file_path(),
        "processor": procname,
        "bitness": bitness,
        "imagebase": fmt_addr(ida_nalt.get_imagebase()),
        "entry_points": entries[:20],
        "file_type": file_type,
    }


@api_method("get_segments")
def _get_segments(**params):
    result = []
    for ea in idautils.Segments():
        seg = ida_segment.getseg(ea)
        result.append({
            "start": fmt_addr(seg.start_ea),
            "end": fmt_addr(seg.end_ea),
            "name": ida_segment.get_segm_name(seg),
            "class": ida_segment.get_segm_class(seg),
            "size": seg.end_ea - seg.start_ea,
            "perm": seg.perm,
            "bitness": seg.bitness * 16 + 16,
        })
    return result


@api_method("get_functions")
def _get_functions(**params):
    start = parse_addr(params.get("start", "0x0"))
    end = parse_addr(params.get("end", "0xFFFFFFFFFFFFFFFF"))
    limit = int(params.get("limit", 0))
    result = []
    for ea in idautils.Functions(start, end):
        func = ida_funcs.get_func(ea)
        result.append({
            "address": fmt_addr(ea),
            "name": ida_funcs.get_func_name(ea),
            "size": func.size() if func else 0,
        })
        if limit and len(result) >= limit:
            break
    return result


@api_method("get_imports")
def _get_imports(**params):
    result = []
    nimps = ida_nalt.get_import_module_qty()
    for i in range(nimps):
        module_name = ida_nalt.get_import_module_name(i)
        entries = []

        def cb(ea, name, ordinal):
            entries.append({
                "address": fmt_addr(ea),
                "name": name or "",
                "ordinal": ordinal,
            })
            return True

        ida_nalt.enum_import_names(i, cb)
        result.append({"module": module_name, "entries": entries})
    return result


@api_method("get_exports")
def _get_exports(**params):
    result = []
    for i, ordinal, ea, name in idautils.Entries():
        result.append({
            "index": i,
            "ordinal": ordinal,
            "address": fmt_addr(ea),
            "name": name or "",
        })
    return result


@api_method("get_strings")
def _get_strings(**params):
    min_length = int(params.get("min_length", 4))
    limit = int(params.get("limit", 1000))
    result = []
    strings = idautils.Strings()
    strings.setup(minlen=min_length)
    for s in strings:
        result.append({
            "address": fmt_addr(s.ea),
            "length": s.length,
            "type": s.strtype,
            "value": str(s),
        })
        if len(result) >= limit:
            break
    return result


@api_method("get_names")
def _get_names(**params):
    limit = int(params.get("limit", 1000))
    result = []
    for ea, name in idautils.Names():
        result.append({"address": fmt_addr(ea), "name": name})
        if len(result) >= limit:
            break
    return result


# ---------------------------------------------------------------------------
# API Methods — Disassembly
# ---------------------------------------------------------------------------

@api_method("get_disassembly")
def _get_disassembly(**params):
    ea = parse_addr(params["address"])
    count = int(params.get("count", 10))
    result = []
    for _ in range(count):
        if ea == idc.BADADDR:
            break
        result.append({
            "address": fmt_addr(ea),
            "disasm": idc.generate_disasm_line(ea, 0),
            "size": idc.get_item_size(ea),
        })
        ea = ida_bytes.next_head(ea, ea + 256)
    return result


@api_method("get_disassembly_range")
def _get_disassembly_range(**params):
    start = parse_addr(params["start"])
    end = parse_addr(params["end"])
    result = []
    ea = start
    while ea < end and ea != idc.BADADDR:
        result.append({
            "address": fmt_addr(ea),
            "disasm": idc.generate_disasm_line(ea, 0),
            "size": idc.get_item_size(ea),
        })
        ea = ida_bytes.next_head(ea, end)
    return result


@api_method("get_function_disassembly")
def _get_function_disassembly(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    result = []
    for item_ea in idautils.FuncItems(func.start_ea):
        result.append({
            "address": fmt_addr(item_ea),
            "disasm": idc.generate_disasm_line(item_ea, 0),
            "size": idc.get_item_size(item_ea),
        })
    return {
        "function": ida_funcs.get_func_name(func.start_ea),
        "start": fmt_addr(func.start_ea),
        "end": fmt_addr(func.end_ea),
        "instructions": result,
    }


@api_method("get_instruction_info")
def _get_instruction_info(**params):
    ea = parse_addr(params["address"])
    insn = idautils.DecodeInstruction(ea)
    if not insn:
        raise ValueError(f"Cannot decode instruction at {fmt_addr(ea)}")
    max_op = getattr(ida_ua, "UA_MAXOP", 6)
    operands = []
    for i in range(max_op):
        op = insn.ops[i]
        if op.type == ida_ua.o_void:
            break
        operands.append({
            "index": i,
            "type": op.type,
            "value": fmt_addr(op.value) if op.value else "0x0",
            "text": idc.print_operand(ea, i),
        })
    return {
        "address": fmt_addr(ea),
        "mnemonic": idc.print_insn_mnem(ea),
        "size": insn.size,
        "operands": operands,
    }


@api_method("get_operand_value")
def _get_operand_value(**params):
    ea = parse_addr(params["address"])
    n = int(params.get("operand", 0))
    val = idc.get_operand_value(ea, n)
    return {"address": fmt_addr(ea), "operand": n, "value": fmt_addr(val)}


# ---------------------------------------------------------------------------
# API Methods — Decompilation / Hex-Rays
# ---------------------------------------------------------------------------

@api_method("decompile_function")
def _decompile_function(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    cfunc = ida_hexrays.decompile(func.start_ea)
    if not cfunc:
        raise RuntimeError("Decompilation failed")
    return {
        "function": ida_funcs.get_func_name(func.start_ea),
        "address": fmt_addr(func.start_ea),
        "pseudocode": str(cfunc),
    }


@api_method("decompile_address")
def _decompile_address(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function containing address {fmt_addr(ea)}")
    cfunc = ida_hexrays.decompile(func.start_ea)
    if not cfunc:
        raise RuntimeError("Decompilation failed")
    return {
        "function": ida_funcs.get_func_name(func.start_ea),
        "address": fmt_addr(func.start_ea),
        "queried_address": fmt_addr(ea),
        "pseudocode": str(cfunc),
    }


@api_method("get_local_variables")
def _get_local_variables(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    cfunc = ida_hexrays.decompile(func.start_ea)
    if not cfunc:
        raise RuntimeError("Decompilation failed")
    lvars = cfunc.get_lvars()
    result = []
    for lvar in lvars:
        result.append({
            "name": lvar.name,
            "type": str(lvar.type()),
            "is_arg": lvar.is_arg_var,
            "is_result": lvar.is_result_var if hasattr(lvar, 'is_result_var') else False,
        })
    return result


@api_method("rename_local_variable")
def _rename_local_variable(**params):
    ea = parse_addr(params["address"])
    old_name = params["old_name"]
    new_name = params["new_name"]
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    cfunc = ida_hexrays.decompile(func.start_ea)
    if not cfunc:
        raise RuntimeError("Decompilation failed")
    lvars = cfunc.get_lvars()
    for i, lvar in enumerate(lvars):
        if lvar.name == old_name:
            lvi = ida_hexrays.lvar_saved_info_t()
            lvi.ll = lvar
            lvi.name = new_name
            lvi.flags = ida_hexrays.LVINF_NAME
            ida_hexrays.modify_user_lvars(func.start_ea, ida_hexrays.lvar_uservec_t(lvi))
            return {"success": True, "old_name": old_name, "new_name": new_name}
    raise ValueError(f"Local variable '{old_name}' not found")


@api_method("set_local_variable_type")
def _set_local_variable_type(**params):
    ea = parse_addr(params["address"])
    var_name = params["name"]
    type_str = params["type"]
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    cfunc = ida_hexrays.decompile(func.start_ea)
    if not cfunc:
        raise RuntimeError("Decompilation failed")
    tif = ida_typeinf.tinfo_t()
    if not ida_typeinf.parse_decl(tif, None, type_str + ";", 0):
        raise ValueError(f"Cannot parse type '{type_str}'")
    lvars = cfunc.get_lvars()
    for lvar in lvars:
        if lvar.name == var_name:
            lvi = ida_hexrays.lvar_saved_info_t()
            lvi.ll = lvar
            lvi.type = tif
            lvi.flags = ida_hexrays.LVINF_TYPE
            ida_hexrays.modify_user_lvars(func.start_ea, ida_hexrays.lvar_uservec_t(lvi))
            return {"success": True, "name": var_name, "type": type_str}
    raise ValueError(f"Local variable '{var_name}' not found")


@api_method("get_decompiler_comments")
def _get_decompiler_comments(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    cfunc = ida_hexrays.decompile(func.start_ea)
    if not cfunc:
        raise RuntimeError("Decompilation failed")
    treeitems = cfunc.treeitems
    comments = {}
    cmt_type = getattr(ida_hexrays, "cmt_retrieval_type_t", None)
    cmt_retrieve = getattr(cmt_type, "retrieve_always", 0) if cmt_type is not None else 0
    get_cmt = getattr(cfunc, "get_user_cmt", None)
    if not get_cmt:
        return comments
    for i in range(treeitems.size()):
        item = treeitems[i]
        try:
            cmt = get_cmt(item, cmt_retrieve)
        except TypeError:
            try:
                cmt = get_cmt(item)
            except Exception:
                break
        except Exception:
            break
        if cmt:
            comments[fmt_addr(item.ea)] = cmt
    return comments


# ---------------------------------------------------------------------------
# API Methods — Functions
# ---------------------------------------------------------------------------

@api_method("get_function_info")
def _get_function_info(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    return {
        "start": fmt_addr(func.start_ea),
        "end": fmt_addr(func.end_ea),
        "name": ida_funcs.get_func_name(func.start_ea),
        "size": func.size(),
        "flags": func.flags,
        "is_thunk": bool(func.flags & ida_funcs.FUNC_THUNK),
        "is_library": bool(func.flags & ida_funcs.FUNC_LIB),
        "frame_size": idc.get_func_attr(func.start_ea, idc.FUNCATTR_FRSIZE),
        "comment": ida_funcs.get_func_cmt(func, False) or "",
        "repeatable_comment": ida_funcs.get_func_cmt(func, True) or "",
    }


@api_method("get_function_by_name")
def _get_function_by_name(**params):
    name = params["name"]
    ea = ida_name.get_name_ea(idc.BADADDR, name)
    if ea == idc.BADADDR:
        raise ValueError(f"Name '{name}' not found")
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"'{name}' at {fmt_addr(ea)} is not a function")
    return {
        "start": fmt_addr(func.start_ea),
        "end": fmt_addr(func.end_ea),
        "name": ida_funcs.get_func_name(func.start_ea),
        "size": func.size(),
    }


@api_method("create_function")
def _create_function(**params):
    start = parse_addr(params["start"])
    end = parse_addr(params.get("end", "0")) or idc.BADADDR
    ok = ida_funcs.add_func(start, end)
    if not ok:
        raise RuntimeError(f"Failed to create function at {fmt_addr(start)}")
    return {"success": True, "address": fmt_addr(start)}


@api_method("delete_function")
def _delete_function(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    ok = ida_funcs.del_func(func.start_ea)
    return {"success": ok, "address": fmt_addr(ea)}


@api_method("set_function_bounds")
def _set_function_bounds(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    if "start" in params:
        new_start = parse_addr(params["start"])
        ida_funcs.set_func_start(func.start_ea, new_start)
    if "end" in params:
        new_end = parse_addr(params["end"])
        ida_funcs.set_func_end(func.start_ea, new_end)
    return {"success": True}


@api_method("get_function_comment")
def _get_function_comment(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    repeatable = bool(params.get("repeatable", False))
    cmt = ida_funcs.get_func_cmt(func, repeatable)
    return {"address": fmt_addr(ea), "comment": cmt or "", "repeatable": repeatable}


@api_method("set_function_comment")
def _set_function_comment(**params):
    ea = parse_addr(params["address"])
    comment = params["comment"]
    repeatable = bool(params.get("repeatable", False))
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    ida_funcs.set_func_cmt(func, comment, repeatable)
    return {"success": True}


@api_method("get_function_flags")
def _get_function_flags(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    return {
        "address": fmt_addr(ea),
        "flags": func.flags,
        "is_thunk": bool(func.flags & ida_funcs.FUNC_THUNK),
        "is_library": bool(func.flags & ida_funcs.FUNC_LIB),
        "is_far": bool(func.flags & ida_funcs.FUNC_FAR),
        "is_static": bool(func.flags & ida_funcs.FUNC_STATICDEF),
        "uses_frame_pointer": bool(func.flags & ida_funcs.FUNC_FRAME),
    }


# ---------------------------------------------------------------------------
# API Methods — Cross-References
# ---------------------------------------------------------------------------

@api_method("get_xrefs_to")
def _get_xrefs_to(**params):
    ea = parse_addr(params["address"])
    result = []
    for xref in idautils.XrefsTo(ea):
        result.append({
            "from": fmt_addr(xref.frm),
            "to": fmt_addr(xref.to),
            "type": xref.type,
            "is_code": xref.iscode,
        })
    return result


@api_method("get_xrefs_from")
def _get_xrefs_from(**params):
    ea = parse_addr(params["address"])
    result = []
    for xref in idautils.XrefsFrom(ea):
        result.append({
            "from": fmt_addr(xref.frm),
            "to": fmt_addr(xref.to),
            "type": xref.type,
            "is_code": xref.iscode,
        })
    return result


@api_method("get_code_refs_to")
def _get_code_refs_to(**params):
    ea = parse_addr(params["address"])
    return [{"address": fmt_addr(ref)} for ref in idautils.CodeRefsTo(ea, True)]


@api_method("get_code_refs_from")
def _get_code_refs_from(**params):
    ea = parse_addr(params["address"])
    return [{"address": fmt_addr(ref)} for ref in idautils.CodeRefsFrom(ea, True)]


@api_method("get_data_refs_to")
def _get_data_refs_to(**params):
    ea = parse_addr(params["address"])
    return [{"address": fmt_addr(ref)} for ref in idautils.DataRefsTo(ea)]


@api_method("get_call_graph")
def _get_call_graph(**params):
    ea = parse_addr(params["address"])
    depth = int(params.get("depth", 1))
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")

    def get_callees(func_ea, current_depth):
        if current_depth <= 0:
            return []
        result = []
        func = ida_funcs.get_func(func_ea)
        if not func:
            return []
        for item_ea in idautils.FuncItems(func_ea):
            for ref in idautils.CodeRefsFrom(item_ea, False):
                called_func = ida_funcs.get_func(ref)
                if called_func and called_func.start_ea != func_ea:
                    entry = {
                        "address": fmt_addr(called_func.start_ea),
                        "name": ida_funcs.get_func_name(called_func.start_ea),
                    }
                    if current_depth > 1:
                        entry["callees"] = get_callees(called_func.start_ea, current_depth - 1)
                    result.append(entry)
        # Deduplicate by address
        seen = set()
        deduped = []
        for r in result:
            if r["address"] not in seen:
                seen.add(r["address"])
                deduped.append(r)
        return deduped

    callers = []
    for xref in idautils.CodeRefsTo(func.start_ea, True):
        caller_func = ida_funcs.get_func(xref)
        if caller_func and caller_func.start_ea != func.start_ea:
            callers.append({
                "address": fmt_addr(caller_func.start_ea),
                "name": ida_funcs.get_func_name(caller_func.start_ea),
            })
    # Deduplicate callers
    seen = set()
    callers_deduped = []
    for c in callers:
        if c["address"] not in seen:
            seen.add(c["address"])
            callers_deduped.append(c)

    return {
        "function": ida_funcs.get_func_name(func.start_ea),
        "address": fmt_addr(func.start_ea),
        "callers": callers_deduped,
        "callees": get_callees(func.start_ea, depth),
    }


# ---------------------------------------------------------------------------
# API Methods — Naming
# ---------------------------------------------------------------------------

@api_method("get_name_at")
def _get_name_at(**params):
    ea = parse_addr(params["address"])
    name = ida_name.get_name(ea)
    return {"address": fmt_addr(ea), "name": name or ""}


@api_method("set_name_at")
def _set_name_at(**params):
    ea = parse_addr(params["address"])
    name = params["name"]
    flags = int(params.get("flags", ida_name.SN_CHECK))
    ok = ida_name.set_name(ea, name, flags)
    return {"success": ok, "address": fmt_addr(ea), "name": name}


@api_method("rename_function")
def _rename_function(**params):
    ea = parse_addr(params["address"])
    new_name = params["name"]
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    ok = ida_name.set_name(func.start_ea, new_name, ida_name.SN_CHECK)
    return {"success": ok, "address": fmt_addr(func.start_ea), "name": new_name}


@api_method("demangle_name")
def _demangle_name(**params):
    name = params["name"]
    demangled = ida_name.demangle_name(name, idc.get_inf_attr(idc.INF_SHORT_DN))
    return {"mangled": name, "demangled": demangled or name}


@api_method("list_names")
def _list_names(**params):
    pattern = params.get("pattern", "")
    limit = int(params.get("limit", 100))
    result = []
    for ea, name in idautils.Names():
        if pattern and pattern.lower() not in name.lower():
            continue
        result.append({"address": fmt_addr(ea), "name": name})
        if len(result) >= limit:
            break
    return result


# ---------------------------------------------------------------------------
# API Methods — Types & Structures
# ---------------------------------------------------------------------------

@api_method("get_type_at")
def _get_type_at(**params):
    ea = parse_addr(params["address"])
    t = idc.get_type(ea)
    return {"address": fmt_addr(ea), "type": t or ""}


@api_method("set_type_at")
def _set_type_at(**params):
    ea = parse_addr(params["address"])
    type_str = params["type"]
    tif = ida_typeinf.tinfo_t()
    til = ida_typeinf.get_idati()
    if not ida_typeinf.parse_decl(tif, til, type_str + ";", 0):
        raise ValueError(f"Cannot parse type declaration: {type_str}")
    ok = ida_typeinf.apply_tinfo(ea, tif, ida_typeinf.TINFO_DEFINITE)
    return {"success": ok, "address": fmt_addr(ea), "type": type_str}


@api_method("parse_type_declaration")
def _parse_type_declaration(**params):
    decl = params["declaration"]
    tif = ida_typeinf.tinfo_t()
    til = ida_typeinf.get_idati()
    ok = ida_typeinf.parse_decl(tif, til, decl + ";", 0)
    if not ok:
        raise ValueError(f"Cannot parse: {decl}")
    return {"success": True, "type": str(tif)}


@api_method("get_local_types")
def _get_local_types(**params):
    limit = int(params.get("limit", 100))
    til = ida_typeinf.get_idati()
    try:
        count = ida_typeinf.get_ordinal_qty(til)
    except (AttributeError, TypeError):
        count = 0
    result = []
    for ordinal in range(1, count + 1):
        tif = ida_typeinf.tinfo_t()
        if tif.get_numbered_type(til, ordinal):
            name = tif.get_type_name()
            result.append({
                "ordinal": ordinal,
                "name": name or f"type_{ordinal}",
                "type": str(tif),
            })
        if len(result) >= limit:
            break
    return result


@api_method("create_struct")
def _create_struct(**params):
    name = params["name"]
    tif = ida_typeinf.tinfo_t()
    udt = ida_typeinf.udt_type_data_t()
    tif.create_udt(udt)
    tif.set_named_type(ida_typeinf.get_idati(), name)
    return {"success": True, "name": name}


@api_method("add_struct_member")
def _add_struct_member(**params):
    struct_name = params["struct_name"]
    member_name = params["member_name"]
    member_type_str = params["member_type"]
    offset = int(params.get("offset", -1))

    til = ida_typeinf.get_idati()
    tif = ida_typeinf.tinfo_t()
    if not tif.get_named_type(til, struct_name):
        raise ValueError(f"Structure '{struct_name}' not found")

    udt = ida_typeinf.udt_type_data_t()
    if not tif.get_udt_details(udt):
        raise ValueError(f"'{struct_name}' is not a structure")

    member_tif = ida_typeinf.tinfo_t()
    if not ida_typeinf.parse_decl(member_tif, til, member_type_str + ";", 0):
        raise ValueError(f"Cannot parse member type: {member_type_str}")

    udm = ida_typeinf.udm_t()
    udm.name = member_name
    udm.type = member_tif
    if offset >= 0:
        udm.offset = offset * 8  # bits

    tif.add_udm(udm)
    tif.set_named_type(til, struct_name)
    return {"success": True, "struct": struct_name, "member": member_name}


@api_method("create_enum")
def _create_enum(**params):
    name = params["name"]
    bitness = int(params.get("bitness", 0))
    tif = ida_typeinf.tinfo_t()
    enum_data = ida_typeinf.enum_type_data_t()
    if bitness:
        enum_data.calc_nbytes = bitness // 8
    tif.create_enum(enum_data)
    tif.set_named_type(ida_typeinf.get_idati(), name)
    return {"success": True, "name": name}


@api_method("add_enum_member")
def _add_enum_member(**params):
    enum_name = params["enum_name"]
    member_name = params["member_name"]
    value = int(params["value"])

    til = ida_typeinf.get_idati()
    tif = ida_typeinf.tinfo_t()
    if not tif.get_named_type(til, enum_name):
        raise ValueError(f"Enum '{enum_name}' not found")

    enum_data = ida_typeinf.enum_type_data_t()
    if not tif.get_enum_details(enum_data):
        raise ValueError(f"'{enum_name}' is not an enum")

    member = ida_typeinf.enum_member_t()
    member.name = member_name
    member.value = value
    enum_data.push_back(member)

    tif.create_enum(enum_data)
    tif.set_named_type(til, enum_name)
    return {"success": True, "enum": enum_name, "member": member_name, "value": value}


# ---------------------------------------------------------------------------
# API Methods — Segments
# ---------------------------------------------------------------------------

@api_method("get_segment_info")
def _get_segment_info(**params):
    ea = parse_addr(params["address"])
    seg = ida_segment.getseg(ea)
    if not seg:
        raise ValueError(f"No segment at {fmt_addr(ea)}")
    return {
        "start": fmt_addr(seg.start_ea),
        "end": fmt_addr(seg.end_ea),
        "name": ida_segment.get_segm_name(seg),
        "class": ida_segment.get_segm_class(seg),
        "size": seg.end_ea - seg.start_ea,
        "perm": seg.perm,
        "bitness": seg.bitness * 16 + 16,
        "type": seg.type,
        "align": seg.align,
    }


@api_method("list_segments")
def _list_segments(**params):
    return _get_segments()


@api_method("create_segment")
def _create_segment(**params):
    start = parse_addr(params["start"])
    end = parse_addr(params["end"])
    name = params.get("name", "")
    sclass = params.get("class", "DATA")
    ok = ida_segment.add_segm(0, start, end, name, sclass)
    return {"success": bool(ok), "start": fmt_addr(start), "end": fmt_addr(end)}


@api_method("set_segment_name")
def _set_segment_name(**params):
    ea = parse_addr(params["address"])
    name = params["name"]
    seg = ida_segment.getseg(ea)
    if not seg:
        raise ValueError(f"No segment at {fmt_addr(ea)}")
    ida_segment.set_segm_name(seg, name)
    return {"success": True, "address": fmt_addr(ea), "name": name}


# ---------------------------------------------------------------------------
# API Methods — Bytes & Patching
# ---------------------------------------------------------------------------

@api_method("read_bytes")
def _read_bytes(**params):
    ea = parse_addr(params["address"])
    size = int(params.get("size", 16))
    data = ida_bytes.get_bytes(ea, size)
    if data is None:
        raise ValueError(f"Cannot read {size} bytes at {fmt_addr(ea)}")
    return {
        "address": fmt_addr(ea),
        "size": size,
        "hex": data.hex(),
        "bytes": list(data),
    }


@api_method("patch_bytes")
def _patch_bytes(**params):
    ea = parse_addr(params["address"])
    hex_str = params["hex"]
    data = bytes.fromhex(hex_str)
    ida_bytes.patch_bytes(ea, data)
    return {"success": True, "address": fmt_addr(ea), "size": len(data)}


@api_method("get_byte_at")
def _get_byte_at(**params):
    ea = parse_addr(params["address"])
    return {"address": fmt_addr(ea), "value": ida_bytes.get_byte(ea)}


@api_method("get_dword_at")
def _get_dword_at(**params):
    ea = parse_addr(params["address"])
    return {"address": fmt_addr(ea), "value": ida_bytes.get_dword(ea)}


@api_method("get_qword_at")
def _get_qword_at(**params):
    ea = parse_addr(params["address"])
    return {"address": fmt_addr(ea), "value": fmt_addr(ida_bytes.get_qword(ea))}


# ---------------------------------------------------------------------------
# API Methods — Search
# ---------------------------------------------------------------------------

@api_method("find_bytes_pattern")
def _find_bytes_pattern(**params):
    pattern = params["pattern"]
    start = parse_addr(params.get("start", "0x0"))
    end = parse_addr(params.get("end", "0xFFFFFFFFFFFFFFFF"))
    limit = int(params.get("limit", 10))

    # Convert hex pattern to binary search format
    result = []
    ea = start
    for _ in range(limit):
        ea = ida_bytes.bin_search(
            ea, end,
            bytes.fromhex(pattern.replace(" ", "")),
            None,
            ida_bytes.BIN_SEARCH_FORWARD | ida_bytes.BIN_SEARCH_NOCASE,
        )
        if ea == idc.BADADDR:
            break
        result.append({"address": fmt_addr(ea)})
        ea += 1
    return result


@api_method("find_text")
def _find_text(**params):
    text = params["text"]
    start = parse_addr(params.get("start", "0x0"))
    limit = int(params.get("limit", 10))
    result = []
    ea = start
    for _ in range(limit):
        ea = ida_search.find_text(ea, 0, 0, text, ida_search.SEARCH_DOWN)
        if ea == idc.BADADDR:
            break
        result.append({
            "address": fmt_addr(ea),
            "disasm": idc.generate_disasm_line(ea, 0),
        })
        ea = ida_bytes.next_head(ea, ea + 256)
    return result


@api_method("find_code")
def _find_code(**params):
    ea = parse_addr(params["address"])
    direction = ida_search.SEARCH_DOWN if params.get("direction", "down") == "down" else ida_search.SEARCH_UP
    found = ida_search.find_code(ea, direction)
    return {"address": fmt_addr(found) if found != idc.BADADDR else None}


@api_method("find_data")
def _find_data(**params):
    ea = parse_addr(params["address"])
    direction = ida_search.SEARCH_DOWN if params.get("direction", "down") == "down" else ida_search.SEARCH_UP
    found = ida_search.find_data(ea, direction)
    return {"address": fmt_addr(found) if found != idc.BADADDR else None}


@api_method("find_immediate")
def _find_immediate(**params):
    value = parse_addr(params["value"])
    start = parse_addr(params.get("start", "0x0"))
    limit = int(params.get("limit", 10))
    result = []
    ea = start
    for _ in range(limit):
        ea, _ = ida_search.find_imm(ea, ida_search.SEARCH_DOWN, value)
        if ea == idc.BADADDR:
            break
        result.append({
            "address": fmt_addr(ea),
            "disasm": idc.generate_disasm_line(ea, 0),
        })
        ea = ida_bytes.next_head(ea, ea + 256)
    return result


# ---------------------------------------------------------------------------
# API Methods — Comments
# ---------------------------------------------------------------------------

@api_method("get_comment")
def _get_comment(**params):
    ea = parse_addr(params["address"])
    cmt = idc.get_cmt(ea, 0)
    return {"address": fmt_addr(ea), "comment": cmt or ""}


@api_method("set_comment")
def _set_comment(**params):
    ea = parse_addr(params["address"])
    comment = params["comment"]
    idc.set_cmt(ea, comment, 0)
    return {"success": True, "address": fmt_addr(ea)}


@api_method("get_repeatable_comment")
def _get_repeatable_comment(**params):
    ea = parse_addr(params["address"])
    cmt = idc.get_cmt(ea, 1)
    return {"address": fmt_addr(ea), "comment": cmt or ""}


@api_method("set_repeatable_comment")
def _set_repeatable_comment(**params):
    ea = parse_addr(params["address"])
    comment = params["comment"]
    idc.set_cmt(ea, comment, 1)
    return {"success": True, "address": fmt_addr(ea)}


# ---------------------------------------------------------------------------
# API Methods — Debugger
# ---------------------------------------------------------------------------

@api_method("start_debugger")
def _start_debugger(**params):
    args = params.get("args", "")
    path = params.get("path", "")
    ok = ida_dbg.start_process(path, args)
    return {"success": ok}


@api_method("pause_debugger")
def _pause_debugger(**params):
    ok = ida_dbg.suspend_process()
    return {"success": ok}


@api_method("continue_debugger")
def _continue_debugger(**params):
    ok = ida_dbg.continue_process()
    return {"success": ok}


@api_method("step_into")
def _step_into(**params):
    ok = ida_dbg.step_into()
    return {"success": ok}


@api_method("step_over")
def _step_over(**params):
    ok = ida_dbg.step_over()
    return {"success": ok}


@api_method("add_breakpoint")
def _add_breakpoint(**params):
    ea = parse_addr(params["address"])
    ok = ida_dbg.add_bpt(ea)
    return {"success": ok, "address": fmt_addr(ea)}


@api_method("get_registers")
def _get_registers(**params):
    names = params.get("names", [])
    if isinstance(names, str):
        names = [n.strip() for n in names.split(",")]
    if not names:
        # Common register names
        names = ["eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp", "eip",
                 "rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp", "rip"]
    result = {}
    for name in names:
        rv = ida_dbg.regval_t()
        if ida_dbg.get_reg_val(name, rv):
            result[name] = fmt_addr(rv.ival)
    return result


@api_method("read_debug_memory")
def _read_debug_memory(**params):
    ea = parse_addr(params["address"])
    size = int(params.get("size", 16))
    buf = b"\x00" * size
    read = ida_dbg.read_dbg_memory(ea, buf, size)
    return {
        "address": fmt_addr(ea),
        "size": read,
        "hex": buf[:read].hex(),
        "bytes": list(buf[:read]),
    }


# ---------------------------------------------------------------------------
# API Methods — Graph & Navigation
# ---------------------------------------------------------------------------

@api_method("jump_to_address")
def _jump_to_address(**params):
    ea = parse_addr(params["address"])
    ok = ida_kernwin.jumpto(ea)
    return {"success": ok, "address": fmt_addr(ea)}


@api_method("get_cursor_position")
def _get_cursor_position(**params):
    ea = ida_kernwin.get_screen_ea()
    name = ida_name.get_name(ea)
    func = ida_funcs.get_func(ea)
    return {
        "address": fmt_addr(ea),
        "name": name or "",
        "function": ida_funcs.get_func_name(func.start_ea) if func else "",
    }


@api_method("get_function_flowchart")
def _get_function_flowchart(**params):
    ea = parse_addr(params["address"])
    func = ida_funcs.get_func(ea)
    if not func:
        raise ValueError(f"No function at {fmt_addr(ea)}")
    fc = ida_gdl.FlowChart(func)
    blocks = []
    for block in fc:
        succs = [fmt_addr(s.start_ea) for s in block.succs()]
        preds = [fmt_addr(p.start_ea) for p in block.preds()]
        blocks.append({
            "start": fmt_addr(block.start_ea),
            "end": fmt_addr(block.end_ea),
            "size": block.end_ea - block.start_ea,
            "successors": succs,
            "predecessors": preds,
        })
    return {
        "function": ida_funcs.get_func_name(func.start_ea),
        "address": fmt_addr(func.start_ea),
        "blocks": blocks,
        "block_count": len(blocks),
    }


# ---------------------------------------------------------------------------
# API Methods — Scripting
# ---------------------------------------------------------------------------

@api_method("execute_script")
def _execute_script(**params):
    script = params["script"]
    local_ns = {
        "idc": idc,
        "idautils": idautils,
        "ida_funcs": ida_funcs,
        "ida_name": ida_name,
        "ida_bytes": ida_bytes,
        "ida_segment": ida_segment,
        "ida_nalt": ida_nalt,
        "ida_typeinf": ida_typeinf,
        "ida_hexrays": ida_hexrays,
        "ida_ua": ida_ua,
        "ida_search": ida_search,
        "ida_kernwin": ida_kernwin,
        "ida_ida": ida_ida,
        "ida_idp": ida_idp,
    }
    exec(script, {"__builtins__": __builtins__, **local_ns}, local_ns)
    result = local_ns.get("result", None)
    if result is not None:
        return {"result": result}
    return {"success": True}


@api_method("eval_expression")
def _eval_expression(**params):
    expression = params["expression"]
    result = eval(expression)
    return {"result": result if isinstance(result, (int, float, str, bool, list, dict, type(None))) else str(result)}


# ---------------------------------------------------------------------------
# JSON-RPC Handler
# ---------------------------------------------------------------------------

class JsonRpcHandler(BaseHTTPRequestHandler):
    """Handle JSON-RPC 2.0 requests."""

    def log_message(self, format, *args):
        ida_kernwin.msg(f"[IDA-MCP] {format % args}\n")

    def _send_json(self, data, status=200):
        body = json.dumps(data).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._send_json({"status": "ok", "plugin": "ida-mcp", "methods": list(_api_methods.keys())})
        else:
            self._send_json({"error": "Not found"}, 404)

    def do_POST(self):
        content_len = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(content_len)

        try:
            request = json.loads(body)
        except json.JSONDecodeError:
            self._send_json({
                "jsonrpc": "2.0",
                "error": {"code": -32700, "message": "Parse error"},
                "id": None,
            })
            return

        req_id = request.get("id")
        method = request.get("method", "")
        params = request.get("params", {})

        if method not in _api_methods:
            self._send_json({
                "jsonrpc": "2.0",
                "error": {"code": -32601, "message": f"Method not found: {method}"},
                "id": req_id,
            })
            return

        try:
            result = _api_methods[method](**params)
            self._send_json({"jsonrpc": "2.0", "result": result, "id": req_id})
        except Exception as e:
            self._send_json({
                "jsonrpc": "2.0",
                "error": {"code": -32000, "message": str(e)},
                "id": req_id,
            })


# ---------------------------------------------------------------------------
# Plugin class
# ---------------------------------------------------------------------------

class IdaMcpPlugin(ida_idaapi.plugin_t):
    flags = ida_idaapi.PLUGIN_KEEP
    comment = "MCP Server for IDA Pro — exposes IDA APIs via JSON-RPC"
    help = "Exposes IDA Pro functionality via MCP protocol"
    wanted_name = "IDA MCP"
    wanted_hotkey = "Ctrl-Shift-M"

    def init(self):
        self.server = None
        self.server_thread = None
        self._start_server()
        return ida_idaapi.PLUGIN_KEEP

    def _start_server(self):
        host = DEFAULT_HOST
        port = DEFAULT_PORT
        try:
            self.server = HTTPServer((host, port), JsonRpcHandler)
            self.server_thread = threading.Thread(
                target=self.server.serve_forever,
                daemon=True,
                name="IDA-MCP-Server",
            )
            self.server_thread.start()
            ida_kernwin.msg(f"[IDA-MCP] Plugin loaded, JSON-RPC server on {host}:{port}\n")
            ida_kernwin.msg(f"[IDA-MCP] {len(_api_methods)} API methods registered\n")
        except Exception as e:
            ida_kernwin.msg(f"[IDA-MCP] Failed to start server: {e}\n")

    def _stop_server(self):
        if self.server:
            self.server.shutdown()
            ida_kernwin.msg("[IDA-MCP] Server stopped\n")

    def run(self, arg):
        if self.server:
            self._stop_server()
            self.server = None
            ida_kernwin.msg("[IDA-MCP] Server toggled OFF\n")
        else:
            self._start_server()
            ida_kernwin.msg("[IDA-MCP] Server toggled ON\n")

    def term(self):
        self._stop_server()


def PLUGIN_ENTRY():
    return IdaMcpPlugin()
