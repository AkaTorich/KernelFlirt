// ============================================================================
// GeneratePatFiles — generates IDA-compatible .pat files from MSVC .lib archives.
//
// Usage:
//   dotnet run --project tools/GeneratePatFiles.csproj [output_dir]
//
// Scans standard MSVC/UCRT lib paths, parses COFF archive members,
// extracts function names + first N bytes of code, writes .pat files.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

class Program
{
    // How many leading bytes to emit per signature (IDA standard is 32)
    const int PatternLength = 32;

    // Minimum function code size to include
    const int MinCodeSize = 8;

    static void Main(string[] args)
    {
        var outputDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "UI", "plugins", "FLIRTpat");

        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"Output directory: {outputDir}");
        Console.WriteLine();

        // Discover MSVC and UCRT lib paths
        var libFiles = DiscoverLibs();

        if (libFiles.Count == 0)
        {
            Console.WriteLine("ERROR: No .lib files found. Is Visual Studio installed?");
            return;
        }

        int totalSigs = 0;
        int totalFiles = 0;

        foreach (var (libPath, patName) in libFiles)
        {
            Console.Write($"Processing {patName,-30} <- {Path.GetFileName(libPath)}...");

            try
            {
                var sigs = ExtractSignaturesFromLib(libPath);
                if (sigs.Count == 0)
                {
                    Console.WriteLine(" 0 functions (skipped)");
                    continue;
                }

                var patPath = Path.Combine(outputDir, patName + ".pat");
                WritePatFile(patPath, sigs);
                Console.WriteLine($" {sigs.Count} functions");
                totalSigs += sigs.Count;
                totalFiles++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($" ERROR: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Done: {totalSigs} signatures in {totalFiles} .pat files -> {outputDir}");
    }

    // ── Lib discovery ────────────────────────────────────────────────────────

    static List<(string path, string patName)> DiscoverLibs()
    {
        var result = new List<(string, string)>();

        // MSVC libs
        var vsBase = @"C:\Program Files\Microsoft Visual Studio\2022";
        if (Directory.Exists(vsBase))
        {
            // Find newest MSVC toolset
            foreach (var edition in new[] { "Enterprise", "Professional", "Community", "BuildTools" })
            {
                var edPath = Path.Combine(vsBase, edition, "VC", "Tools", "MSVC");
                if (!Directory.Exists(edPath)) continue;

                var versions = Directory.GetDirectories(edPath)
                    .OrderByDescending(d => d)
                    .ToList();

                if (versions.Count == 0) continue;
                var libDir = Path.Combine(versions[0], "lib", "x64");
                if (!Directory.Exists(libDir)) break;

                var ver = Path.GetFileName(versions[0]);
                Console.WriteLine($"Found MSVC {ver} ({edition})");
                Console.WriteLine($"  {libDir}");

                TryAdd(result, libDir, "libcmt.lib", $"msvc{ver}_libcmt_x64");
                TryAdd(result, libDir, "libvcruntime.lib", $"msvc{ver}_libvcruntime_x64");
                TryAdd(result, libDir, "libcpmt.lib", $"msvc{ver}_libcpmt_x64");
                TryAdd(result, libDir, "libconcrt.lib", $"msvc{ver}_libconcrt_x64");

                // x86
                var libDir32 = Path.Combine(versions[0], "lib", "x86");
                if (Directory.Exists(libDir32))
                {
                    Console.WriteLine($"  {libDir32}");
                    TryAdd(result, libDir32, "libcmt.lib", $"msvc{ver}_libcmt_x86");
                    TryAdd(result, libDir32, "libvcruntime.lib", $"msvc{ver}_libvcruntime_x86");
                    TryAdd(result, libDir32, "libcpmt.lib", $"msvc{ver}_libcpmt_x86");
                    TryAdd(result, libDir32, "libconcrt.lib", $"msvc{ver}_libconcrt_x86");
                }

                break; // use first found edition
            }
        }

        // UCRT libs (Windows SDK)
        var kitsBase = @"C:\Program Files (x86)\Windows Kits\10\Lib";
        if (Directory.Exists(kitsBase))
        {
            var versions = Directory.GetDirectories(kitsBase)
                .OrderByDescending(d => d)
                .ToList();

            foreach (var verDir in versions)
            {
                var ucrtDir = Path.Combine(verDir, "ucrt", "x64");
                if (!Directory.Exists(ucrtDir)) continue;

                var ver = Path.GetFileName(verDir);
                Console.WriteLine($"Found UCRT SDK {ver}");
                Console.WriteLine($"  {ucrtDir}");

                TryAdd(result, ucrtDir, "libucrt.lib", $"ucrt{ver}_libucrt_x64");

                // x86
                var ucrtDir32 = Path.Combine(verDir, "ucrt", "x86");
                if (Directory.Exists(ucrtDir32))
                {
                    Console.WriteLine($"  {ucrtDir32}");
                    TryAdd(result, ucrtDir32, "libucrt.lib", $"ucrt{ver}_libucrt_x86");
                }

                break; // use newest
            }
        }

        Console.WriteLine();
        return result;
    }

    static void TryAdd(List<(string, string)> list, string dir, string fileName, string patName)
    {
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path))
            list.Add((path, patName));
    }

    // ── COFF .lib archive parser ─────────────────────────────────────────────
    //
    // .lib format (COFF archive):
    //   "!<arch>\n" signature (8 bytes)
    //   Repeated archive members:
    //     60-byte header: Name/16 Date/12 UID/6 GID/6 Mode/8 Size/10 End/2
    //     Payload (padded to 2-byte boundary)
    //
    // Each COFF object member contains:
    //   COFF header (20 bytes): Machine, NumberOfSections, ...
    //   Section headers (40 bytes each): Name, VirtualSize, ...
    //   Section data (raw bytes)
    //   Symbol table

    static List<PatEntry> ExtractSignaturesFromLib(string libPath)
    {
        var result = new List<PatEntry>();
        var data = File.ReadAllBytes(libPath);

        // Validate archive signature
        if (data.Length < 8) return result;
        var sig = Encoding.ASCII.GetString(data, 0, 8);
        if (sig != "!<arch>\n") return result;

        int pos = 8;

        // Track long name table (for "//" member)
        byte[]? longNames = null;

        while (pos + 60 <= data.Length)
        {
            // Parse archive member header (60 bytes)
            var nameField = Encoding.ASCII.GetString(data, pos, 16).TrimEnd();
            var sizeField = Encoding.ASCII.GetString(data, pos + 48, 10).Trim();
            var endField = Encoding.ASCII.GetString(data, pos + 58, 2);

            if (endField != "`\n")
            {
                // Alignment issue — try to recover
                pos += 2;
                continue;
            }

            if (!int.TryParse(sizeField, out int memberSize))
                break;

            int memberStart = pos + 60;
            int memberEnd = memberStart + memberSize;

            // Long name table
            if (nameField == "//" || nameField.StartsWith("//"))
            {
                longNames = new byte[memberSize];
                Array.Copy(data, memberStart, longNames, 0, Math.Min(memberSize, data.Length - memberStart));
                pos = Align2(memberEnd);
                continue;
            }

            // Skip linker members ("/" and second "/")
            if (nameField == "/" || nameField.StartsWith("/ "))
            {
                pos = Align2(memberEnd);
                continue;
            }

            // Resolve member name
            string memberName = nameField.TrimEnd('/');
            if (nameField.StartsWith("/") && int.TryParse(nameField.AsSpan(1), out int nameOffset))
            {
                if (longNames != null && nameOffset < longNames.Length)
                    memberName = ReadNullTerminated(longNames, nameOffset);
            }

            // Try to parse as COFF object
            if (memberStart + 20 <= data.Length)
            {
                var sigs = ParseCoffObject(data, memberStart, memberSize, memberName);
                result.AddRange(sigs);
            }

            pos = Align2(memberEnd);
        }

        // Deduplicate by name (keep longest pattern)
        var deduped = result
            .GroupBy(s => s.Name)
            .Select(g => g.OrderByDescending(s => s.CodeBytes.Length).First())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return deduped;
    }

    static List<PatEntry> ParseCoffObject(byte[] data, int baseOff, int memberSize, string memberName)
    {
        var result = new List<PatEntry>();
        int limit = Math.Min(baseOff + memberSize, data.Length);

        if (baseOff + 20 > limit) return result;

        ushort machine = ReadU16(data, baseOff);
        // Accept x64 (0x8664) and x86 (0x14C)
        if (machine != 0x8664 && machine != 0x14C) return result;

        ushort numSections = ReadU16(data, baseOff + 2);
        int symTableOffset = (int)ReadU32(data, baseOff + 8) + baseOff;
        int numSymbols = (int)ReadU32(data, baseOff + 12);

        if (numSections == 0 || numSections > 256) return result;
        if (symTableOffset <= 0 || symTableOffset >= limit) return result;

        // Parse section headers
        int sectHdrOff = baseOff + 20; // COFF header is 20 bytes (no optional header in .obj)
        var sections = new List<CoffSection>();

        for (int i = 0; i < numSections; i++)
        {
            int off = sectHdrOff + i * 40;
            if (off + 40 > limit) break;

            var sect = new CoffSection
            {
                Name = Encoding.ASCII.GetString(data, off, 8).TrimEnd('\0'),
                VirtualSize = ReadU32(data, off + 8),
                RawDataSize = ReadU32(data, off + 16),
                RawDataOffset = (int)ReadU32(data, off + 20) + baseOff,
                Characteristics = ReadU32(data, off + 36),
                RelocOffset = (int)ReadU32(data, off + 24) + baseOff,
                NumRelocs = ReadU16(data, off + 32)
            };
            sections.Add(sect);
        }

        // Parse string table (right after symbol table)
        int strTableOff = symTableOffset + numSymbols * 18;
        int strTableSize = 0;
        if (strTableOff + 4 <= limit)
            strTableSize = (int)ReadU32(data, strTableOff);

        // Parse symbol table — find EXTERNAL function symbols with section defined
        // Each symbol is 18 bytes
        int si = 0;
        while (si < numSymbols)
        {
            int symOff = symTableOffset + si * 18;
            if (symOff + 18 > limit) break;

            // Symbol name (first 8 bytes)
            string symName;
            uint nameZero = ReadU32(data, symOff);
            if (nameZero == 0)
            {
                // Long name: offset into string table
                uint nameStrOff = ReadU32(data, symOff + 4);
                int absOff = strTableOff + (int)nameStrOff;
                symName = absOff < limit ? ReadNullTerminated(data, absOff) : "";
            }
            else
            {
                symName = Encoding.ASCII.GetString(data, symOff, 8).TrimEnd('\0');
            }

            // uint value = ReadU32(data, symOff + 8);
            short sectionNumber = (short)ReadU16(data, symOff + 12); // 1-based
            // ushort type = ReadU16(data, symOff + 14);
            byte storageClass = data[symOff + 16];
            byte numAux = data[symOff + 17];

            // We want: external (storageClass=2) function symbols with valid section
            // type has function bit (0x20) but not all compilers set it, so check code section instead
            if (storageClass == 2 && sectionNumber >= 1 && sectionNumber <= sections.Count)
            {
                var sect = sections[sectionNumber - 1];
                bool isCode = (sect.Characteristics & 0x20) != 0 // IMAGE_SCN_CNT_CODE
                           || (sect.Characteristics & 0x20000000) != 0; // IMAGE_SCN_MEM_EXECUTE

                if (isCode && sect.RawDataSize >= MinCodeSize && sect.RawDataOffset > 0
                    && sect.RawDataOffset + sect.RawDataSize <= limit
                    && !string.IsNullOrEmpty(symName))
                {
                    // Read section code bytes
                    int codeLen = (int)Math.Min(sect.RawDataSize, PatternLength);
                    var codeBytes = new byte[codeLen];
                    Array.Copy(data, sect.RawDataOffset, codeBytes, 0, codeLen);

                    // Collect relocation offsets within the pattern range
                    // (these bytes will be wildcards since they're fixup targets)
                    var relocOffsets = new HashSet<int>();
                    for (int ri = 0; ri < sect.NumRelocs; ri++)
                    {
                        int rOff = sect.RelocOffset + ri * 10;
                        if (rOff + 10 > limit) break;
                        uint relocVa = ReadU32(data, rOff);
                        // Each reloc covers 4 bytes (rel32/addr32)
                        for (int b = 0; b < 4; b++)
                        {
                            int byteOff = (int)relocVa + b;
                            if (byteOff >= 0 && byteOff < codeLen)
                                relocOffsets.Add(byteOff);
                        }
                    }

                    // Clean up symbol name
                    var cleanName = CleanSymbolName(symName);
                    if (cleanName.Length > 0)
                    {
                        result.Add(new PatEntry
                        {
                            Name = cleanName,
                            CodeBytes = codeBytes,
                            RelocOffsets = relocOffsets,
                            TotalSize = (int)sect.RawDataSize
                        });
                    }
                }
            }

            si += 1 + numAux; // skip auxiliary symbols
        }

        return result;
    }

    // ── .pat file writer ─────────────────────────────────────────────────────

    static void WritePatFile(string path, List<PatEntry> entries)
    {
        using var writer = new StreamWriter(path, false, Encoding.ASCII);

        foreach (var entry in entries)
        {
            var sb = new StringBuilder();

            // Leading pattern bytes (hex pairs, ".." for wildcards)
            int len = Math.Min(entry.CodeBytes.Length, PatternLength);
            for (int i = 0; i < len; i++)
            {
                if (entry.RelocOffsets.Contains(i))
                    sb.Append("..");
                else
                    sb.Append(entry.CodeBytes[i].ToString("X2"));
            }

            // Pad to PatternLength if shorter
            for (int i = len; i < PatternLength; i++)
                sb.Append("..");

            // CRC length and CRC16 (00 0000 — skip for now)
            sb.Append(" 00 0000");

            // Total length : offset
            sb.Append($" {entry.TotalSize:X4}:0000");

            // Function name
            sb.Append($" {entry.Name}");

            writer.WriteLine(sb.ToString());
        }

        // End marker
        writer.WriteLine("---");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string CleanSymbolName(string name)
    {
        // Strip leading underscore for x64 (MSVC x64 doesn't use _ prefix, but some symbols have it)
        // Keep C++ mangled names as-is
        if (name.StartsWith("__imp_") || name.StartsWith("__IMPORT_"))
            return ""; // skip import thunks

        // Skip internal/compiler-generated symbols
        if (name.StartsWith("$") || name.StartsWith("__xmm@") || name.StartsWith("__ymm@")
            || name.StartsWith("__real@") || name.StartsWith("__mask@"))
            return "";

        return name;
    }

    static ushort ReadU16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    static uint ReadU32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    static string ReadNullTerminated(byte[] data, int offset)
    {
        int end = offset;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    static int Align2(int value) => (value + 1) & ~1;
}

class PatEntry
{
    public string Name { get; init; } = "";
    public byte[] CodeBytes { get; init; } = [];
    public HashSet<int> RelocOffsets { get; init; } = new();
    public int TotalSize { get; init; }
}

class CoffSection
{
    public string Name { get; set; } = "";
    public uint VirtualSize { get; set; }
    public uint RawDataSize { get; set; }
    public int RawDataOffset { get; set; }
    public uint Characteristics { get; set; }
    public int RelocOffset { get; set; }
    public ushort NumRelocs { get; set; }
}
