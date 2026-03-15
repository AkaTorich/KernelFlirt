r"""
Install IDA MCP plugin into IDA Pro plugins directory.

Usage:
  python install_plugin.py [--ida-dir PATH] [--symlink]
  set IDA_DIR=C:\Program Files\IDA Pro 9.0  (optional)
"""

import argparse
import os
import shutil
import sys
from pathlib import Path


def find_ida_dir(env_dir: str | None) -> Path | None:
    if env_dir and os.path.isdir(env_dir):
        return Path(env_dir)
    candidates = [
        Path(os.environ.get("IDA_DIR", "")),
        Path("C:/Program Files/IDA Professional 9.2"),
        Path("C:/Program Files/IDA Pro 9.0"),
        Path("C:/Program Files/IDA 9.0"),
        Path(os.path.expanduser("~/ida-9.0")),
    ]
    for p in candidates:
        if p and p.is_dir() and (p / "plugins").is_dir():
            return p
    return None


def main() -> None:
    parser = argparse.ArgumentParser(description="Install IDA MCP plugin into IDA Pro")
    parser.add_argument(
        "--ida-dir",
        type=Path,
        default=None,
        help="IDA Pro installation directory (default: IDA_DIR env or auto-detect)",
    )
    parser.add_argument(
        "--symlink",
        action="store_true",
        help="Create symlink instead of copy (for development)",
    )
    args = parser.parse_args()

    project_root = Path(__file__).resolve().parent
    plugin_src = project_root / "src" / "ida_mcp" / "plugin" / "ida_mcp_plugin.py"
    if not plugin_src.is_file():
        print(f"Error: plugin not found at {plugin_src}", file=sys.stderr)
        sys.exit(1)

    ida_dir = args.ida_dir or find_ida_dir(os.environ.get("IDA_DIR"))
    if not ida_dir or not (ida_dir / "plugins").is_dir():
        print("Error: IDA Pro directory not found. Set IDA_DIR or use --ida-dir PATH.", file=sys.stderr)
        print("Example: python install_plugin.py --ida-dir \"C:\\Program Files\\IDA Pro 9.0\"", file=sys.stderr)
        sys.exit(1)

    plugins_dir = ida_dir / "plugins"
    plugin_dst = plugins_dir / "ida_mcp.py"

    try:
        if plugin_dst.exists():
            plugin_dst.unlink()
        if args.symlink:
            plugin_dst.symlink_to(plugin_src)
            print(f"Symlink: {plugin_dst} -> {plugin_src}")
        else:
            shutil.copy2(plugin_src, plugin_dst)
            print(f"Copied: {plugin_src.name} -> {plugins_dir}")
        print("Done. Restart IDA Pro or load plugin manually (Ctrl+Shift+M).")
    except OSError as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
