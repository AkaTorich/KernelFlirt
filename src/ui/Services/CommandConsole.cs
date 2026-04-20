using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using KernelFlirt.UI.Models;
using KernelFlirt.UI.ViewModels;

namespace KernelFlirt.UI.Services;

/// <summary>
/// OllyDbg / x64dbg style command console. Parses a command line, evaluates
/// expressions (registers, modules, hex literals, dereference) and dispatches
/// to MainViewModel actions. Returns a short status string to show next to the
/// input.
/// </summary>
public class CommandConsole
{
    private readonly MainViewModel _vm;
    public CommandConsole(MainViewModel vm) { _vm = vm; }

    public async Task<string> ExecuteAsync(string line)
    {
        line = line.Trim();
        if (string.IsNullOrEmpty(line)) return "";
        // Drop leading colon if user typed ":bp ntdll!X"
        if (line.StartsWith(":")) line = line[1..].Trim();

        // Split into command + rest
        int sp = line.IndexOf(' ');
        string cmd = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
        string arg = sp < 0 ? "" : line[(sp + 1)..].Trim();

        try
        {
            switch (cmd)
            {
                case "g":
                case "go":
                case "run":
                    _vm.ContinueExecutionCommand.Execute(null);
                    return "continue";

                case "t":
                case "sti":
                case "stepi":
                    _vm.StepInCommand.Execute(null);
                    return "step into";

                case "p":
                case "sto":
                case "stepo":
                    _vm.StepOverCommand.Execute(null);
                    return "step over";

                case "bp":
                {
                    if (!TryEval(arg, out var addr)) return "err: bad addr";
                    _vm.SetBreakpointAtAddress(addr);
                    return $"bp @ {addr:X}";
                }

                case "bc":
                {
                    if (!TryEval(arg, out var addr)) return "err: bad addr";
                    var bp = _vm.Breakpoints.FirstOrDefault(b => b.Address == addr);
                    if (bp == null) return "no bp at that addr";
                    _vm.RemoveBreakpointByHandle(bp.Handle);
                    return $"bp cleared @ {addr:X}";
                }

                case "bl":
                {
                    if (_vm.Breakpoints.Count == 0) return "no breakpoints";
                    return $"{_vm.Breakpoints.Count} bp(s): " +
                           string.Join(", ", _vm.Breakpoints.Take(4).Select(b => b.AddressHex));
                }

                case "d":
                case "dump":
                {
                    if (!TryEval(arg, out var addr)) return "err: bad addr";
                    _vm.FollowAddressInDump(addr);
                    return $"dump @ {addr:X}";
                }

                case "dis":
                case "u":
                case "disasm":
                {
                    if (!TryEval(arg, out var addr)) return "err: bad addr";
                    _vm.NavigateDisasmTo(addr);
                    return $"disasm @ {addr:X}";
                }

                case "r":
                {
                    // r eax=1   or   r eax
                    var m = Regex.Match(arg, @"^(\w+)\s*(?:=\s*(.+))?$");
                    if (!m.Success) return "usage: r <reg>[=<val>]";
                    string name = m.Groups[1].Value;
                    if (!m.Groups[2].Success)
                    {
                        var reg = _vm.Registers.FirstOrDefault(r =>
                            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) && !r.IsFlag);
                        return reg == null ? $"no reg {name}" : $"{reg.Name} = {reg.ValueHex}";
                    }
                    if (!TryEval(m.Groups[2].Value, out var val)) return "err: bad value";
                    _vm.WriteRegisterValue(name.ToUpperInvariant(), val);
                    return $"{name.ToUpperInvariant()} := {val:X}";
                }

                case "?":
                case "eval":
                {
                    if (!TryEval(arg, out var v)) return "err: bad expr";
                    return $"= 0x{v:X}  ({v}  {(long)v})";
                }

                case "findall":
                case "find":
                {
                    _vm.SearchBinaryCommand.Execute(arg);
                    return $"search: {arg}";
                }

                case "clear":
                case "cls":
                    return "";

                default:
                    return $"unknown: {cmd}";
            }
        }
        catch (Exception ex)
        {
            return $"err: {ex.Message}";
        }
    }

    // ================================================================
    // Expression evaluator: rax+4, [rip+10], ntdll!NtReadFile, 0x1234
    // Supports: + - * /, parens, brackets (mem deref qword), module!sym,
    // register names, hex (0x, trailing h, bare A-F digits), decimal.
    // ================================================================

    public bool TryEval(string expr, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expr)) return false;
        try
        {
            var tokens = Tokenize(expr);
            int i = 0;
            value = ParseExpr(tokens, ref i);
            return i == tokens.Count;
        }
        catch { return false; }
    }

    private enum TT { Num, Reg, ModSym, Op, LParen, RParen, LBrack, RBrack }
    private record Token(TT Kind, string Text, ulong Num);

    private List<Token> Tokenize(string s)
    {
        var toks = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '+' || c == '-' || c == '*' || c == '/')
            {
                toks.Add(new Token(TT.Op, c.ToString(), 0)); i++; continue;
            }
            if (c == '(') { toks.Add(new Token(TT.LParen, "(", 0)); i++; continue; }
            if (c == ')') { toks.Add(new Token(TT.RParen, ")", 0)); i++; continue; }
            if (c == '[') { toks.Add(new Token(TT.LBrack, "[", 0)); i++; continue; }
            if (c == ']') { toks.Add(new Token(TT.RBrack, "]", 0)); i++; continue; }

            // word: register or module!symbol or hex literal
            int start = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '!' || s[i] == '.' || s[i] == '@'))
                i++;
            string word = s[start..i];
            if (word.Contains('!'))
            {
                toks.Add(new Token(TT.ModSym, word, 0));
                continue;
            }
            // Try register first (rax, eax, rip, rflags, ...)
            if (_vm.Registers.Any(r => string.Equals(r.Name, word, StringComparison.OrdinalIgnoreCase) && !r.IsFlag))
            {
                toks.Add(new Token(TT.Reg, word.ToUpperInvariant(), 0));
                continue;
            }
            // Try module base (e.g. "ntdll")
            if (TryResolveModuleBase(word, out var modBase))
            {
                toks.Add(new Token(TT.Num, word, modBase));
                continue;
            }
            // Hex literal: 0x..., ...h, or bare hex
            if (TryParseNumber(word, out var num))
            {
                toks.Add(new Token(TT.Num, word, num));
                continue;
            }
            throw new InvalidOperationException($"bad token: {word}");
        }
        return toks;
    }

    private bool TryParseNumber(string w, out ulong num)
    {
        num = 0;
        string s = w;
        bool hex = false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s[2..]; hex = true; }
        else if (s.EndsWith("h", StringComparison.OrdinalIgnoreCase)) { s = s[..^1]; hex = true; }
        if (!hex)
        {
            // If every char is hex-digit and contains at least one A-F, treat as hex (x64dbg default).
            if (s.All(IsHexDigit)) hex = true;
        }
        if (hex)
            return ulong.TryParse(s, NumberStyles.HexNumber, null, out num);
        return ulong.TryParse(s, NumberStyles.Integer, null, out num);
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private bool TryResolveModuleBase(string name, out ulong addr)
    {
        addr = 0;
        var m = _vm.Modules.FirstOrDefault(x =>
            string.Equals(Path.GetFileNameWithoutExtension(x.Name), name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (m == null) return false;
        addr = m.BaseAddress;
        return true;
    }

    private ulong ResolveModSym(string modSym)
    {
        // "ntdll!NtReadFile" - try dbghelp via VM
        var parts = modSym.Split('!', 2);
        if (parts.Length != 2) return 0;
        var addr = _vm.ResolveSymbolName(parts[0], parts[1]);
        if (addr == 0) throw new InvalidOperationException($"sym not found: {modSym}");
        return addr;
    }

    // Recursive descent: Expr = Term (('+'|'-') Term)* ; Term = Factor (('*'|'/') Factor)*
    private ulong ParseExpr(List<Token> t, ref int i)
    {
        ulong v = ParseTerm(t, ref i);
        while (i < t.Count && t[i].Kind == TT.Op && (t[i].Text == "+" || t[i].Text == "-"))
        {
            var op = t[i].Text; i++;
            ulong r = ParseTerm(t, ref i);
            v = op == "+" ? v + r : v - r;
        }
        return v;
    }
    private ulong ParseTerm(List<Token> t, ref int i)
    {
        ulong v = ParseFactor(t, ref i);
        while (i < t.Count && t[i].Kind == TT.Op && (t[i].Text == "*" || t[i].Text == "/"))
        {
            var op = t[i].Text; i++;
            ulong r = ParseFactor(t, ref i);
            v = op == "*" ? v * r : (r == 0 ? 0 : v / r);
        }
        return v;
    }
    private ulong ParseFactor(List<Token> t, ref int i)
    {
        if (i >= t.Count) throw new InvalidOperationException("unexpected end");
        var tok = t[i];
        if (tok.Kind == TT.Op && tok.Text == "-")
        {
            i++;
            return (ulong)(-(long)ParseFactor(t, ref i));
        }
        if (tok.Kind == TT.LParen)
        {
            i++;
            var v = ParseExpr(t, ref i);
            if (i >= t.Count || t[i].Kind != TT.RParen) throw new InvalidOperationException("missing )");
            i++;
            return v;
        }
        if (tok.Kind == TT.LBrack)
        {
            i++;
            var addr = ParseExpr(t, ref i);
            if (i >= t.Count || t[i].Kind != TT.RBrack) throw new InvalidOperationException("missing ]");
            i++;
            // Dereference 8 bytes at addr
            return _vm.ReadQwordAt(addr);
        }
        if (tok.Kind == TT.Num) { i++; return tok.Num; }
        if (tok.Kind == TT.Reg)
        {
            i++;
            var reg = _vm.Registers.FirstOrDefault(r =>
                string.Equals(r.Name, tok.Text, StringComparison.OrdinalIgnoreCase) && !r.IsFlag);
            return reg?.Value ?? 0;
        }
        if (tok.Kind == TT.ModSym) { i++; return ResolveModSym(tok.Text); }
        throw new InvalidOperationException($"bad factor: {tok.Text}");
    }
}
