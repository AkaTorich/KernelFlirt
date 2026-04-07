# Navigation Bar

The navigation bar is an IDA Pro-style memory map displayed above the tab strip. It shows the section layout of the main module with markers for RIP, breakpoints, and bookmarks.

## Section Colors

| Color | Meaning |
|-------|---------|
| **Blue** | Code sections (.text, IMAGE_SCN_CNT_CODE / IMAGE_SCN_MEM_EXECUTE) |
| **Green** | Writable data (.data, IMAGE_SCN_CNT_INITIALIZED_DATA + IMAGE_SCN_MEM_WRITE) |
| **Yellow** | Read-only data (.rdata, IMAGE_SCN_CNT_INITIALIZED_DATA) |
| **Purple** | Uninitialized data (.bss, IMAGE_SCN_CNT_UNINITIALIZED_DATA) |
| **Gray** | Other sections |

## Markers

| Marker | Meaning |
|--------|---------|
| **Yellow triangle** (top, pointing down) | Current RIP — moves only on step/continue |
| **White triangle** (bottom, pointing up) | Navigation cursor — where you clicked |
| **Red vertical line** | Breakpoint |
| **Orange diamond** | Bookmark / annotation |

## Section Labels

Section names (`.text`, `.rdata`, `.data`, etc.) are displayed directly on the bar when the section is wide enough.

## Interaction

- **Hover** — tooltip shows module name, section name, exact address under cursor, section address range, and flags
- **Click** — navigates disassembly to the exact address (proportional to click position within the section)
- **Proportional layout** — sections are packed side-by-side using square-root scaling so small sections remain visible

## Scope

The navigation bar shows only the **main module** (the debugged executable), not system libraries. This keeps the bar focused and readable.
