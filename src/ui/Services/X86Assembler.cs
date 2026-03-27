using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace KernelFlirt.UI.Services;

/// <summary>
/// Lightweight x86/x64 text assembler built on top of Iced.Intel.
/// Parses common instruction text (e.g. "mov eax, 1", "nop", "jmp 0x401000")
/// and encodes to machine code bytes.  Also accepts raw hex bytes (e.g. "90 90 90").
/// </summary>
public static class X86Assembler
{
    public static (byte[]? bytes, string? error) Assemble(string text, ulong address, bool is32Bit)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return (null, "Empty input");

        // Try hex bytes first: "90 90 90" or "B8 01 00 00 00"
        if (TryParseHexBytes(text, out var hexBytes))
            return (hexBytes, null);

        // Try Iced assembler
        try
        {
            int bitness = is32Bit ? 32 : 64;
            var asm = new Assembler(bitness);

            if (!TryParseInstruction(asm, text, address, is32Bit, out var error))
                return (null, error);

            using var ms = new System.IO.MemoryStream();
            asm.Assemble(new StreamCodeWriter(ms), address);
            return (ms.ToArray(), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static bool TryParseHexBytes(string text, out byte[] bytes)
    {
        bytes = [];
        var parts = text.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var result = new List<byte>();
        foreach (var part in parts)
        {
            string p = part;
            if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                p = p[2..];
            if (p.Length == 0 || p.Length > 2) return false;
            if (!byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out var b))
                return false;
            result.Add(b);
        }
        bytes = result.ToArray();
        return true;
    }

    private static bool TryParseInstruction(Assembler asm, string text, ulong address, bool is32Bit, out string? error)
    {
        error = null;
        var tokens = text.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) { error = "Empty instruction"; return false; }

        string mnemonic = tokens[0].ToLowerInvariant();
        string operandsRaw = tokens.Length > 1 ? tokens[1].Trim() : "";
        string[] operands = string.IsNullOrEmpty(operandsRaw) ? []
            : operandsRaw.Split(',', StringSplitOptions.TrimEntries);

        try
        {
            switch (mnemonic)
            {
                // --- No operands ---
                case "nop": asm.nop(); return true;
                case "ret": asm.ret(); return true;
                case "int3": asm.int3(); return true;
                case "hlt": asm.hlt(); return true;
                case "leave": asm.leave(); return true;
                case "cdq": asm.cdq(); return true;
                case "cwd": asm.cwd(); return true;
                case "cqo": asm.cqo(); return true;
                case "cbw": asm.cbw(); return true;
                case "cwde": asm.cwde(); return true;
                case "cdqe": asm.cdqe(); return true;
                case "pushfq": asm.pushfq(); return true;
                case "popfq": asm.popfq(); return true;
                case "pushfd": asm.pushfd(); return true;
                case "popfd": asm.popfd(); return true;
                case "syscall": asm.syscall(); return true;
                case "rdtsc": asm.rdtsc(); return true;
                case "cpuid": asm.cpuid(); return true;
                case "ud2": asm.ud2(); return true;
                case "cli": asm.cli(); return true;
                case "sti": asm.sti(); return true;
                case "clc": asm.clc(); return true;
                case "stc": asm.stc(); return true;
                case "cmc": asm.cmc(); return true;
                case "cld": asm.cld(); return true;
                case "std": asm.std(); return true;
                case "movsb": asm.movsb(); return true;
                case "movsw": asm.movsw(); return true;
                case "movsd" when operands.Length == 0: asm.movsd(); return true;
                case "movsq": asm.movsq(); return true;
                case "stosb": asm.stosb(); return true;
                case "stosw": asm.stosw(); return true;
                case "stosd": asm.stosd(); return true;
                case "stosq": asm.stosq(); return true;
                case "lodsb": asm.lodsb(); return true;
                case "lodsw": asm.lodsw(); return true;
                case "lodsd": asm.lodsd(); return true;
                case "lodsq": asm.lodsq(); return true;
                case "scasb": asm.scasb(); return true;
                case "scasw": asm.scasw(); return true;
                case "scasd": asm.scasd(); return true;
                case "scasq": asm.scasq(); return true;

                // --- ret imm16 ---
                case "retn":
                    if (operands.Length > 0 && TryParseImm(operands[0], out long retImm))
                    { asm.ret((ushort)retImm); return true; }
                    asm.ret(); return true;

                // --- int N ---
                case "int":
                    if (operands.Length > 0 && TryParseImm(operands[0], out long intN))
                    { asm.@int((byte)intN); return true; }
                    error = "int requires an immediate operand"; return false;

                // --- push/pop ---
                case "push": return DoPush(asm, operands, is32Bit, out error);
                case "pop": return DoPop(asm, operands, is32Bit, out error);

                // --- Unary: inc, dec, not, neg, mul, div, imul(1) ---
                case "inc": case "dec": case "not": case "neg":
                case "mul": case "div":
                    return DoUnary(asm, mnemonic, operands, out error);
                case "imul" when operands.Length == 1:
                    return DoUnary(asm, mnemonic, operands, out error);

                // --- Branches ---
                case "jmp": return DoJmpAbs(asm, operands, address, is32Bit, out error);
                case "call": return DoCallAbs(asm, operands, address, is32Bit, out error);
                case "je": case "jz": return DoJccAbs(asm, 0x84, operands, address, out error);
                case "jne": case "jnz": return DoJccAbs(asm, 0x85, operands, address, out error);
                case "ja": return DoJccAbs(asm, 0x87, operands, address, out error);
                case "jae": case "jnb": return DoJccAbs(asm, 0x83, operands, address, out error);
                case "jb": case "jnae": return DoJccAbs(asm, 0x82, operands, address, out error);
                case "jbe": case "jna": return DoJccAbs(asm, 0x86, operands, address, out error);
                case "jg": return DoJccAbs(asm, 0x8F, operands, address, out error);
                case "jge": case "jnl": return DoJccAbs(asm, 0x8D, operands, address, out error);
                case "jl": case "jnge": return DoJccAbs(asm, 0x8C, operands, address, out error);
                case "jle": case "jng": return DoJccAbs(asm, 0x8E, operands, address, out error);
                case "js": return DoJccAbs(asm, 0x88, operands, address, out error);
                case "jns": return DoJccAbs(asm, 0x89, operands, address, out error);
                case "jo": return DoJccAbs(asm, 0x80, operands, address, out error);
                case "jno": return DoJccAbs(asm, 0x81, operands, address, out error);
                case "jp": case "jpe": return DoJccAbs(asm, 0x8A, operands, address, out error);
                case "jnp": case "jpo": return DoJccAbs(asm, 0x8B, operands, address, out error);

                // --- Two-operand instructions ---
                case "mov": case "add": case "sub": case "xor": case "and": case "or":
                case "cmp": case "test": case "lea":
                case "shl": case "shr": case "sar": case "sal": case "rol": case "ror":
                case "bt": case "bts": case "btr": case "btc":
                case "movzx": case "movsx": case "movsxd":
                case "xchg": case "bsf": case "bsr":
                case "cmove": case "cmovne": case "cmova": case "cmovae":
                case "cmovb": case "cmovbe": case "cmovg": case "cmovge":
                case "cmovl": case "cmovle":
                    return DoTwoOp(asm, mnemonic, operands, is32Bit, out error);

                // db - raw bytes
                case "db":
                    return DoDb(asm, operands, out error);

                default:
                    error = $"Unsupported instruction: {mnemonic}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    #region Immediate parsing

    private static bool TryParseImm(string s, out long value)
    {
        value = 0;
        s = s.Trim().ToLowerInvariant();
        if (s.StartsWith("0x"))
            return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        if (s.EndsWith("h") && long.TryParse(s[..^1], System.Globalization.NumberStyles.HexNumber, null, out value))
            return true;
        if (long.TryParse(s, out value))
            return true;
        if (s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            return long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value);
        return false;
    }

    #endregion

    #region Register helpers

    private static AssemblerRegister64? R64(string s) => s.ToLowerInvariant() switch
    {
        "rax" => rax, "rbx" => rbx, "rcx" => rcx, "rdx" => rdx,
        "rsi" => rsi, "rdi" => rdi, "rbp" => rbp, "rsp" => rsp,
        "r8" => r8, "r9" => r9, "r10" => r10, "r11" => r11,
        "r12" => r12, "r13" => r13, "r14" => r14, "r15" => r15,
        _ => null
    };

    private static AssemblerRegister32? R32(string s) => s.ToLowerInvariant() switch
    {
        "eax" => eax, "ebx" => ebx, "ecx" => ecx, "edx" => edx,
        "esi" => esi, "edi" => edi, "ebp" => ebp, "esp" => esp,
        "r8d" => r8d, "r9d" => r9d, "r10d" => r10d, "r11d" => r11d,
        "r12d" => r12d, "r13d" => r13d, "r14d" => r14d, "r15d" => r15d,
        _ => null
    };

    private static AssemblerRegister16? R16(string s) => s.ToLowerInvariant() switch
    {
        "ax" => ax, "bx" => bx, "cx" => cx, "dx" => dx,
        "si" => si, "di" => di, "bp" => bp, "sp" => sp,
        "r8w" => r8w, "r9w" => r9w, "r10w" => r10w, "r11w" => r11w,
        "r12w" => r12w, "r13w" => r13w, "r14w" => r14w, "r15w" => r15w,
        _ => null
    };

    private static AssemblerRegister8? R8(string s) => s.ToLowerInvariant() switch
    {
        "al" => al, "bl" => bl, "cl" => cl, "dl" => dl,
        "ah" => ah, "bh" => bh, "ch" => ch, "dh" => dh,
        "sil" => sil, "dil" => dil, "bpl" => bpl, "spl" => spl,
        "r8b" => r8b, "r9b" => r9b, "r10b" => r10b, "r11b" => r11b,
        "r12b" => r12b, "r13b" => r13b, "r14b" => r14b, "r15b" => r15b,
        _ => null
    };

    private static bool IsReg(string s) => R64(s) != null || R32(s) != null || R16(s) != null || R8(s) != null;
    private static bool IsMem(string s) => s.Contains('[') && s.Contains(']');

    #endregion

    #region Memory operand building via __[]

    // Build Iced memory operand from text like "[rax+8]", "dword [ebp-4]", "qword ptr [rsp+rcx*8+10h]"
    private static AssemblerMemoryOperand BuildMem(string text, bool is32Bit, int forcedSize = 0)
    {
        string s = text.Trim().ToLowerInvariant();

        // Extract size prefix
        int sizeHint = forcedSize;
        if (s.StartsWith("byte")) { sizeHint = 1; s = s["byte".Length..].Trim(); }
        else if (s.StartsWith("word")) { sizeHint = 2; s = s["word".Length..].Trim(); }
        else if (s.StartsWith("dword")) { sizeHint = 4; s = s["dword".Length..].Trim(); }
        else if (s.StartsWith("qword")) { sizeHint = 8; s = s["qword".Length..].Trim(); }
        s = s.Replace("ptr", "").Trim();

        int lb = s.IndexOf('[');
        int rb = s.IndexOf(']');
        if (lb < 0 || rb < 0) throw new ArgumentException($"Invalid memory operand: {text}");
        string inner = s[(lb + 1)..rb].Trim();

        // Parse: base + index*scale + disp
        AssemblerRegister64? baseR64 = null;
        AssemblerRegister32? baseR32 = null;
        AssemblerRegister64? idxR64 = null;
        AssemblerRegister32? idxR32 = null;
        int scale = 1;
        long disp = 0;

        var parts = inner.Replace("+", " + ").Replace("-", " - ").Replace("*", " * ")
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool negateNext = false;
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p == "+") { negateNext = false; continue; }
            if (p == "-") { negateNext = true; continue; }
            if (p == "*") continue;

            bool isScaled = i + 2 < parts.Length && parts[i + 1] == "*";
            int sc = 1;
            if (isScaled && int.TryParse(parts[i + 2], out sc)) { /* use sc */ }

            var r64 = R64(p);
            var r32 = R32(p);
            if (r64 != null)
            {
                if (isScaled) { idxR64 = r64; scale = sc; i += 2; }
                else if (baseR64 == null) baseR64 = r64;
                else idxR64 = r64;
            }
            else if (r32 != null)
            {
                if (isScaled) { idxR32 = r32; scale = sc; i += 2; }
                else if (baseR32 == null) baseR32 = r32;
                else idxR32 = r32;
            }
            else if (TryParseImm(p, out long v))
            {
                disp += negateNext ? -v : v;
                negateNext = false;
            }
        }

        // Build expression using Iced's overloaded operators
        // We use the __[] syntax: __[reg + idx*scale + disp]
        if (baseR64 != null)
        {
            AssemblerMemoryOperand mem;
            if (idxR64 != null)
                mem = __[baseR64.Value + idxR64.Value * scale + disp];
            else if (disp != 0)
                mem = __[baseR64.Value + disp];
            else
                mem = __[baseR64.Value];

            return ApplySize(mem, sizeHint);
        }
        if (baseR32 != null)
        {
            AssemblerMemoryOperand mem;
            if (idxR32 != null)
                mem = __[baseR32.Value + idxR32.Value * scale + (int)disp];
            else if (disp != 0)
                mem = __[baseR32.Value + (int)disp];
            else
                mem = __[baseR32.Value];

            return ApplySize(mem, sizeHint);
        }
        // Absolute address: [0x401000]
        if (disp != 0)
            return ApplySize(__[disp], sizeHint);

        throw new ArgumentException($"Cannot parse memory operand: {text}");
    }

    private static AssemblerMemoryOperand ApplySize(AssemblerMemoryOperand mem, int size) => size switch
    {
        1 => __byte_ptr[mem],
        2 => __word_ptr[mem],
        4 => __dword_ptr[mem],
        8 => __qword_ptr[mem],
        _ => mem
    };

    #endregion

    #region push/pop

    private static bool DoPush(Assembler asm, string[] ops, bool is32Bit, out string? error)
    {
        error = null;
        if (ops.Length != 1) { error = "push requires 1 operand"; return false; }
        string op = ops[0].Trim();
        if (R64(op) is { } r64) { asm.push(r64); return true; }
        if (R32(op) is { } r32) { asm.push(r32); return true; }
        if (R16(op) is { } r16) { asm.push(r16); return true; }
        if (TryParseImm(op, out long imm)) { asm.push((int)imm); return true; }
        error = $"Cannot parse push operand: {op}"; return false;
    }

    private static bool DoPop(Assembler asm, string[] ops, bool is32Bit, out string? error)
    {
        error = null;
        if (ops.Length != 1) { error = "pop requires 1 operand"; return false; }
        string op = ops[0].Trim();
        if (R64(op) is { } r64) { asm.pop(r64); return true; }
        if (R32(op) is { } r32) { asm.pop(r32); return true; }
        if (R16(op) is { } r16) { asm.pop(r16); return true; }
        error = $"Cannot parse pop operand: {op}"; return false;
    }

    #endregion

    #region Unary (inc, dec, not, neg, mul, div, imul)

    private static bool DoUnary(Assembler asm, string mn, string[] ops, out string? error)
    {
        error = null;
        if (ops.Length != 1) { error = $"{mn} requires 1 operand"; return false; }
        string op = ops[0].Trim();

        if (R64(op) is { } r) { Unary64(asm, mn, r); return true; }
        if (R32(op) is { } r32) { Unary32(asm, mn, r32); return true; }
        if (R16(op) is { } r16) { Unary16(asm, mn, r16); return true; }
        if (R8(op) is { } r8) { Unary8(asm, mn, r8); return true; }
        error = $"Cannot parse {mn} operand: {op}"; return false;
    }

    private static void Unary64(Assembler a, string m, AssemblerRegister64 r) { switch (m) {
        case "inc": a.inc(r); break; case "dec": a.dec(r); break;
        case "not": a.not(r); break; case "neg": a.neg(r); break;
        case "mul": a.mul(r); break; case "div": a.div(r); break;
        case "imul": a.imul(r); break;
    }}
    private static void Unary32(Assembler a, string m, AssemblerRegister32 r) { switch (m) {
        case "inc": a.inc(r); break; case "dec": a.dec(r); break;
        case "not": a.not(r); break; case "neg": a.neg(r); break;
        case "mul": a.mul(r); break; case "div": a.div(r); break;
        case "imul": a.imul(r); break;
    }}
    private static void Unary16(Assembler a, string m, AssemblerRegister16 r) { switch (m) {
        case "inc": a.inc(r); break; case "dec": a.dec(r); break;
        case "not": a.not(r); break; case "neg": a.neg(r); break;
        case "mul": a.mul(r); break; case "div": a.div(r); break;
        case "imul": a.imul(r); break;
    }}
    private static void Unary8(Assembler a, string m, AssemblerRegister8 r) { switch (m) {
        case "inc": a.inc(r); break; case "dec": a.dec(r); break;
        case "not": a.not(r); break; case "neg": a.neg(r); break;
        case "mul": a.mul(r); break; case "div": a.div(r); break;
        case "imul": a.imul(r); break;
    }}

    #endregion

    #region Branches (manual encoding for absolute targets)

    // jmp to absolute address or register
    private static bool DoJmpAbs(Assembler asm, string[] ops, ulong address, bool is32Bit, out string? error)
    {
        error = null;
        if (ops.Length != 1) { error = "jmp requires 1 operand"; return false; }
        string op = ops[0].Trim();

        if (R64(op) is { } r64) { asm.jmp(r64); return true; }
        if (R32(op) is { } r32) { asm.jmp(r32); return true; }

        if (TryParseImm(op, out long target))
        {
            // Encode as E9 rel32
            long rel = target - (long)address - 5;
            if (rel < int.MinValue || rel > int.MaxValue)
            { error = "jmp target too far for rel32"; return false; }
            asm.db(0xE9);
            var relBytes = BitConverter.GetBytes((int)rel);
            asm.db(relBytes);
            return true;
        }

        // jmp [mem]
        if (IsMem(op))
        {
            var mem = BuildMem(op, is32Bit, is32Bit ? 4 : 8);
            asm.jmp(mem);
            return true;
        }

        error = $"Cannot parse jmp operand: {op}"; return false;
    }

    // call to absolute address or register
    private static bool DoCallAbs(Assembler asm, string[] ops, ulong address, bool is32Bit, out string? error)
    {
        error = null;
        if (ops.Length != 1) { error = "call requires 1 operand"; return false; }
        string op = ops[0].Trim();

        if (R64(op) is { } r64) { asm.call(r64); return true; }
        if (R32(op) is { } r32) { asm.call(r32); return true; }

        if (TryParseImm(op, out long target))
        {
            // Encode as E8 rel32
            long rel = target - (long)address - 5;
            if (rel < int.MinValue || rel > int.MaxValue)
            { error = "call target too far for rel32"; return false; }
            asm.db(0xE8);
            var relBytes = BitConverter.GetBytes((int)rel);
            asm.db(relBytes);
            return true;
        }

        // call [mem]
        if (IsMem(op))
        {
            var mem = BuildMem(op, is32Bit, is32Bit ? 4 : 8);
            asm.call(mem);
            return true;
        }

        error = $"Cannot parse call operand: {op}"; return false;
    }

    // Conditional jump: 0F 8x rel32 (cc is second opcode byte like 0x84=je, 0x85=jne)
    private static bool DoJccAbs(Assembler asm, byte cc, string[] ops, ulong address, out string? error)
    {
        error = null;
        if (ops.Length != 1) { error = "Conditional jump requires 1 operand"; return false; }
        if (!TryParseImm(ops[0].Trim(), out long target))
        { error = $"Cannot parse jump target: {ops[0]}"; return false; }

        // Try short form first (2 bytes: 7x rel8)
        long relShort = target - (long)address - 2;
        if (relShort >= -128 && relShort <= 127)
        {
            byte shortOp = (byte)(0x70 | (cc & 0x0F));
            asm.db(shortOp, (byte)(sbyte)relShort);
            return true;
        }

        // Near form: 0F 8x rel32 (6 bytes)
        long rel = target - (long)address - 6;
        if (rel < int.MinValue || rel > int.MaxValue)
        { error = "Jump target too far for rel32"; return false; }
        asm.db(0x0F, cc);
        asm.db(BitConverter.GetBytes((int)rel));
        return true;
    }

    #endregion

    #region Two-operand instructions

    private static bool DoTwoOp(Assembler asm, string mn, string[] ops, bool is32Bit, out string? error)
    {
        error = null;
        if (ops.Length != 2) { error = $"{mn} requires 2 operands (got {ops.Length})"; return false; }

        string dst = ops[0].Trim();
        string src = ops[1].Trim();
        bool srcIsImm = TryParseImm(src, out long srcImm);

        try
        {
            // dst = r64
            if (R64(dst) is { } d64)
            {
                if (R64(src) is { } s64) { TwoR64R64(asm, mn, d64, s64); return true; }
                if (srcIsImm) { TwoR64Imm(asm, mn, d64, (int)srcImm); return true; }
                if (IsMem(src)) { TwoR64Mem(asm, mn, d64, BuildMem(src, is32Bit, 8)); return true; }
            }
            // dst = r32
            if (R32(dst) is { } d32)
            {
                if (R32(src) is { } s32) { TwoR32R32(asm, mn, d32, s32); return true; }
                if (R8(src) is { } s8 && mn is "movzx" or "movsx") { TwoR32R8(asm, mn, d32, s8); return true; }
                if (R16(src) is { } s16 && mn is "movzx" or "movsx") { TwoR32R16(asm, mn, d32, s16); return true; }
                if (srcIsImm) { TwoR32Imm(asm, mn, d32, (int)srcImm); return true; }
                if (IsMem(src)) { TwoR32Mem(asm, mn, d32, BuildMem(src, is32Bit, 4)); return true; }
            }
            // dst = r16
            if (R16(dst) is { } d16)
            {
                if (R16(src) is { } s16) { TwoR16R16(asm, mn, d16, s16); return true; }
                if (srcIsImm) { TwoR16Imm(asm, mn, d16, (short)srcImm); return true; }
            }
            // dst = r8
            if (R8(dst) is { } d8)
            {
                if (R8(src) is { } s8) { TwoR8R8(asm, mn, d8, s8); return true; }
                if (srcIsImm) { TwoR8Imm(asm, mn, d8, (byte)srcImm); return true; }
            }
            // dst = [mem]
            if (IsMem(dst))
            {
                if (R64(src) is { } s64) { TwoMemR64(asm, mn, BuildMem(dst, is32Bit, 8), s64); return true; }
                if (R32(src) is { } s32) { TwoMemR32(asm, mn, BuildMem(dst, is32Bit, 4), s32); return true; }
                if (R16(src) is { } s16) { TwoMemR16(asm, mn, BuildMem(dst, is32Bit, 2), s16); return true; }
                if (R8(src) is { } s8) { TwoMemR8(asm, mn, BuildMem(dst, is32Bit, 1), s8); return true; }
                if (srcIsImm)
                {
                    // Determine size from prefix in dst
                    string lower = dst.ToLowerInvariant();
                    int sz = lower.StartsWith("byte") ? 1 : lower.StartsWith("word") ? 2
                           : lower.StartsWith("qword") ? 8 : 4;
                    var mem = BuildMem(dst, is32Bit, sz);
                    TwoMemImm(asm, mn, mem, (int)srcImm, sz);
                    return true;
                }
            }

            error = $"Cannot encode: {mn} {dst}, {src}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // --- r64, r64 ---
    private static void TwoR64R64(Assembler a, string m, AssemblerRegister64 d, AssemblerRegister64 s) { switch (m) {
        case "mov": a.mov(d, s); break; case "add": a.add(d, s); break;
        case "sub": a.sub(d, s); break; case "xor": a.xor(d, s); break;
        case "and": a.and(d, s); break; case "or": a.or(d, s); break;
        case "cmp": a.cmp(d, s); break; case "test": a.test(d, s); break;
        case "xchg": a.xchg(d, s); break;
        case "bt": a.bt(d, s); break; case "bts": a.bts(d, s); break;
        case "btr": a.btr(d, s); break; case "btc": a.btc(d, s); break;
        case "bsf": a.bsf(d, s); break; case "bsr": a.bsr(d, s); break;
        case "cmove": a.cmove(d, s); break; case "cmovne": a.cmovne(d, s); break;
        case "cmova": a.cmova(d, s); break; case "cmovae": a.cmovae(d, s); break;
        case "cmovb": a.cmovb(d, s); break; case "cmovbe": a.cmovbe(d, s); break;
        case "cmovg": a.cmovg(d, s); break; case "cmovge": a.cmovge(d, s); break;
        case "cmovl": a.cmovl(d, s); break; case "cmovle": a.cmovle(d, s); break;
        default: throw new NotSupportedException($"{m} r64,r64");
    }}

    // --- r64, imm ---
    private static void TwoR64Imm(Assembler a, string m, AssemblerRegister64 d, int imm) { switch (m) {
        case "mov": a.mov(d, imm); break; case "add": a.add(d, imm); break;
        case "sub": a.sub(d, imm); break; case "xor": a.xor(d, imm); break;
        case "and": a.and(d, imm); break; case "or": a.or(d, imm); break;
        case "cmp": a.cmp(d, imm); break; case "test": a.test(d, imm); break;
        case "shl": a.shl(d, (byte)imm); break; case "shr": a.shr(d, (byte)imm); break;
        case "sar": a.sar(d, (byte)imm); break; case "sal": a.sal(d, (byte)imm); break;
        case "rol": a.rol(d, (byte)imm); break; case "ror": a.ror(d, (byte)imm); break;
        default: throw new NotSupportedException($"{m} r64,imm");
    }}

    // --- r64, [mem] ---
    private static void TwoR64Mem(Assembler a, string m, AssemblerRegister64 d, AssemblerMemoryOperand mem) { switch (m) {
        case "mov": a.mov(d, mem); break; case "add": a.add(d, mem); break;
        case "sub": a.sub(d, mem); break; case "xor": a.xor(d, mem); break;
        case "and": a.and(d, mem); break; case "or": a.or(d, mem); break;
        case "cmp": a.cmp(d, mem); break;
        case "lea": a.lea(d, mem); break;
        default: throw new NotSupportedException($"{m} r64,[mem]");
    }}

    // --- r32, r32 ---
    private static void TwoR32R32(Assembler a, string m, AssemblerRegister32 d, AssemblerRegister32 s) { switch (m) {
        case "mov": a.mov(d, s); break; case "add": a.add(d, s); break;
        case "sub": a.sub(d, s); break; case "xor": a.xor(d, s); break;
        case "and": a.and(d, s); break; case "or": a.or(d, s); break;
        case "cmp": a.cmp(d, s); break; case "test": a.test(d, s); break;
        case "xchg": a.xchg(d, s); break;
        case "bt": a.bt(d, s); break; case "bsf": a.bsf(d, s); break; case "bsr": a.bsr(d, s); break;
        case "cmove": a.cmove(d, s); break; case "cmovne": a.cmovne(d, s); break;
        case "cmova": a.cmova(d, s); break; case "cmovae": a.cmovae(d, s); break;
        case "cmovb": a.cmovb(d, s); break; case "cmovbe": a.cmovbe(d, s); break;
        case "cmovg": a.cmovg(d, s); break; case "cmovge": a.cmovge(d, s); break;
        case "cmovl": a.cmovl(d, s); break; case "cmovle": a.cmovle(d, s); break;
        default: throw new NotSupportedException($"{m} r32,r32");
    }}

    // --- r32, imm ---
    private static void TwoR32Imm(Assembler a, string m, AssemblerRegister32 d, int imm) { switch (m) {
        case "mov": a.mov(d, imm); break; case "add": a.add(d, imm); break;
        case "sub": a.sub(d, imm); break; case "xor": a.xor(d, imm); break;
        case "and": a.and(d, imm); break; case "or": a.or(d, imm); break;
        case "cmp": a.cmp(d, imm); break; case "test": a.test(d, imm); break;
        case "shl": a.shl(d, (byte)imm); break; case "shr": a.shr(d, (byte)imm); break;
        case "sar": a.sar(d, (byte)imm); break; case "sal": a.sal(d, (byte)imm); break;
        case "rol": a.rol(d, (byte)imm); break; case "ror": a.ror(d, (byte)imm); break;
        default: throw new NotSupportedException($"{m} r32,imm");
    }}

    // --- r32, [mem] ---
    private static void TwoR32Mem(Assembler a, string m, AssemblerRegister32 d, AssemblerMemoryOperand mem) { switch (m) {
        case "mov": a.mov(d, mem); break; case "add": a.add(d, mem); break;
        case "sub": a.sub(d, mem); break; case "xor": a.xor(d, mem); break;
        case "and": a.and(d, mem); break; case "or": a.or(d, mem); break;
        case "cmp": a.cmp(d, mem); break;
        case "lea": a.lea(d, mem); break;
        default: throw new NotSupportedException($"{m} r32,[mem]");
    }}

    // --- r32, r8 (movzx/movsx) ---
    private static void TwoR32R8(Assembler a, string m, AssemblerRegister32 d, AssemblerRegister8 s) { switch (m) {
        case "movzx": a.movzx(d, s); break; case "movsx": a.movsx(d, s); break;
        default: throw new NotSupportedException($"{m} r32,r8");
    }}

    // --- r32, r16 (movzx/movsx) ---
    private static void TwoR32R16(Assembler a, string m, AssemblerRegister32 d, AssemblerRegister16 s) { switch (m) {
        case "movzx": a.movzx(d, s); break; case "movsx": a.movsx(d, s); break;
        default: throw new NotSupportedException($"{m} r32,r16");
    }}

    // --- r16, r16 ---
    private static void TwoR16R16(Assembler a, string m, AssemblerRegister16 d, AssemblerRegister16 s) { switch (m) {
        case "mov": a.mov(d, s); break; case "add": a.add(d, s); break;
        case "sub": a.sub(d, s); break; case "xor": a.xor(d, s); break;
        case "and": a.and(d, s); break; case "or": a.or(d, s); break;
        case "cmp": a.cmp(d, s); break; case "test": a.test(d, s); break;
        default: throw new NotSupportedException($"{m} r16,r16");
    }}

    // --- r16, imm ---
    private static void TwoR16Imm(Assembler a, string m, AssemblerRegister16 d, short imm) { switch (m) {
        case "mov": a.mov(d, imm); break; case "add": a.add(d, imm); break;
        case "sub": a.sub(d, imm); break; case "xor": a.xor(d, imm); break;
        case "and": a.and(d, imm); break; case "or": a.or(d, imm); break;
        case "cmp": a.cmp(d, imm); break; case "test": a.test(d, imm); break;
        default: throw new NotSupportedException($"{m} r16,imm");
    }}

    // --- r8, r8 ---
    private static void TwoR8R8(Assembler a, string m, AssemblerRegister8 d, AssemblerRegister8 s) { switch (m) {
        case "mov": a.mov(d, s); break; case "add": a.add(d, s); break;
        case "sub": a.sub(d, s); break; case "xor": a.xor(d, s); break;
        case "and": a.and(d, s); break; case "or": a.or(d, s); break;
        case "cmp": a.cmp(d, s); break; case "test": a.test(d, s); break;
        default: throw new NotSupportedException($"{m} r8,r8");
    }}

    // --- r8, imm ---
    private static void TwoR8Imm(Assembler a, string m, AssemblerRegister8 d, byte imm) { switch (m) {
        case "mov": a.mov(d, imm); break; case "add": a.add(d, imm); break;
        case "sub": a.sub(d, imm); break; case "xor": a.xor(d, imm); break;
        case "and": a.and(d, imm); break; case "or": a.or(d, imm); break;
        case "cmp": a.cmp(d, imm); break; case "test": a.test(d, imm); break;
        default: throw new NotSupportedException($"{m} r8,imm");
    }}

    // --- [mem], reg ---
    private static void TwoMemR64(Assembler a, string m, AssemblerMemoryOperand mem, AssemblerRegister64 s) { switch (m) {
        case "mov": a.mov(mem, s); break; case "add": a.add(mem, s); break;
        case "sub": a.sub(mem, s); break; case "xor": a.xor(mem, s); break;
        case "and": a.and(mem, s); break; case "or": a.or(mem, s); break;
        case "cmp": a.cmp(mem, s); break;
        default: throw new NotSupportedException($"{m} [mem],r64");
    }}

    private static void TwoMemR32(Assembler a, string m, AssemblerMemoryOperand mem, AssemblerRegister32 s) { switch (m) {
        case "mov": a.mov(mem, s); break; case "add": a.add(mem, s); break;
        case "sub": a.sub(mem, s); break; case "xor": a.xor(mem, s); break;
        case "and": a.and(mem, s); break; case "or": a.or(mem, s); break;
        case "cmp": a.cmp(mem, s); break;
        default: throw new NotSupportedException($"{m} [mem],r32");
    }}

    private static void TwoMemR16(Assembler a, string m, AssemblerMemoryOperand mem, AssemblerRegister16 s) { switch (m) {
        case "mov": a.mov(mem, s); break;
        default: throw new NotSupportedException($"{m} [mem],r16");
    }}

    private static void TwoMemR8(Assembler a, string m, AssemblerMemoryOperand mem, AssemblerRegister8 s) { switch (m) {
        case "mov": a.mov(mem, s); break;
        default: throw new NotSupportedException($"{m} [mem],r8");
    }}

    // --- [mem], imm ---
    private static void TwoMemImm(Assembler a, string m, AssemblerMemoryOperand mem, int imm, int size)
    {
        switch (m)
        {
            case "mov": a.mov(mem, imm); break;
            case "add": a.add(mem, imm); break;
            case "sub": a.sub(mem, imm); break;
            case "xor": a.xor(mem, imm); break;
            case "and": a.and(mem, imm); break;
            case "or": a.or(mem, imm); break;
            case "cmp": a.cmp(mem, imm); break;
            default: throw new NotSupportedException($"{m} [mem],imm");
        }
    }

    #endregion

    #region db

    private static bool DoDb(Assembler asm, string[] ops, out string? error)
    {
        error = null;
        var bytes = new List<byte>();
        foreach (var op in ops)
        {
            if (TryParseImm(op.Trim(), out long val))
                bytes.Add((byte)val);
            else { error = $"Cannot parse db operand: {op}"; return false; }
        }
        asm.db(bytes.ToArray());
        return true;
    }

    #endregion
}
