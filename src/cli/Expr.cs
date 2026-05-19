// Минимальный парсер выражений в стиле WinDbg/x64dbg:
//   rip + 0x10            — арифметика
//   [rsp + 8]             — qword разыменование памяти
//   kernel32!CreateFileA  — символ (через SymbolService)
//   rax * 8 + base        — скобки, * / + - сдвиги &, |, ^
//
// Числа по умолчанию hex (как в WinDbg). Регистры берутся из последнего
// прочитанного ReadRegisters (Program.Sess.LastRegs). Память читается через
// KfClient (для qword).
namespace KernelFlirt.Cli;

internal sealed class ExprEvaluator
{
    private readonly KfClient _client;
    private readonly SymbolService _syms;
    private readonly Func<KF_REGISTERS?> _getRegs;     // снимок регистров на момент вычисления
    private readonly Func<uint> _getPid;
    private readonly Func<bool> _is32Bit;

    public ExprEvaluator(KfClient client, SymbolService syms,
                         Func<KF_REGISTERS?> getRegs, Func<uint> getPid, Func<bool> is32Bit)
    { _client = client; _syms = syms; _getRegs = getRegs; _getPid = getPid; _is32Bit = is32Bit; }

    public bool TryEval(string src, out ulong value)
    {
        value = 0;
        try
        {
            var lex = new Lexer(src);
            var parser = new Parser(lex, this);
            value = parser.ParseExpr();
            return parser.AtEnd;
        }
        catch { return false; }
    }

    // ── Lexer ────────────────────────────────────────────────────────────

    private enum TokKind { Num, Ident, LParen, RParen, LBracket, RBracket,
                           Plus, Minus, Star, Slash, Shl, Shr, And, Or, Xor, End }

    private readonly struct Tok
    {
        public readonly TokKind Kind;
        public readonly ulong Num;
        public readonly string? Text;
        public Tok(TokKind k, ulong n = 0, string? t = null) { Kind = k; Num = n; Text = t; }
    }

    private sealed class Lexer
    {
        private readonly string _src;
        private int _pos;
        public Lexer(string src) { _src = src; }

        public Tok Next()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
            if (_pos >= _src.Length) return new Tok(TokKind.End);
            char c = _src[_pos];

            if (c == '(') { _pos++; return new Tok(TokKind.LParen); }
            if (c == ')') { _pos++; return new Tok(TokKind.RParen); }
            if (c == '[') { _pos++; return new Tok(TokKind.LBracket); }
            if (c == ']') { _pos++; return new Tok(TokKind.RBracket); }
            if (c == '+') { _pos++; return new Tok(TokKind.Plus); }
            if (c == '-') { _pos++; return new Tok(TokKind.Minus); }
            if (c == '*') { _pos++; return new Tok(TokKind.Star); }
            if (c == '/') { _pos++; return new Tok(TokKind.Slash); }
            if (c == '&') { _pos++; return new Tok(TokKind.And); }
            if (c == '|') { _pos++; return new Tok(TokKind.Or); }
            if (c == '^') { _pos++; return new Tok(TokKind.Xor); }
            if (c == '<' && _pos + 1 < _src.Length && _src[_pos + 1] == '<')
            { _pos += 2; return new Tok(TokKind.Shl); }
            if (c == '>' && _pos + 1 < _src.Length && _src[_pos + 1] == '>')
            { _pos += 2; return new Tok(TokKind.Shr); }

            // 0x... — явный hex
            if (c == '0' && _pos + 1 < _src.Length && (_src[_pos + 1] == 'x' || _src[_pos + 1] == 'X'))
            {
                int s = _pos + 2; _pos = s;
                while (_pos < _src.Length && IsHex(_src[_pos])) _pos++;
                ulong v = ulong.Parse(_src.AsSpan(s, _pos - s),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                return new Tok(TokKind.Num, v);
            }

            // Идентификатор: [_a-zA-Z][_a-zA-Z0-9!]* — допускаем `!` для kernel32!CreateFileA.
            if (char.IsLetter(c) || c == '_')
            {
                int s = _pos++;
                while (_pos < _src.Length &&
                       (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_' || _src[_pos] == '!'))
                    _pos++;
                return new Tok(TokKind.Ident, 0, _src[s.._pos]);
            }

            // Число по умолчанию hex (как WinDbg). Принимаем [0-9a-fA-F]+ с опциональным 'h' в конце.
            if (IsHex(c))
            {
                int s = _pos;
                while (_pos < _src.Length && IsHex(_src[_pos])) _pos++;
                int end = _pos;
                if (_pos < _src.Length && (_src[_pos] == 'h' || _src[_pos] == 'H')) _pos++;
                ulong v = ulong.Parse(_src.AsSpan(s, end - s),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                return new Tok(TokKind.Num, v);
            }

            throw new FormatException($"unexpected char '{c}' at {_pos}");
        }

        private static bool IsHex(char c)
            => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }

    // ── Parser (recursive descent) ───────────────────────────────────────

    private sealed class Parser
    {
        private readonly Lexer _lex;
        private readonly ExprEvaluator _outer;
        private Tok _cur;

        public bool AtEnd => _cur.Kind == TokKind.End;

        public Parser(Lexer lex, ExprEvaluator outer)
        { _lex = lex; _outer = outer; _cur = _lex.Next(); }

        private void Eat() => _cur = _lex.Next();

        // expr := bitor
        // bitor := xor (('|') xor)*
        // xor := bitand (('^') bitand)*
        // bitand := shift (('&') shift)*
        // shift := add (('<<' | '>>') add)*
        // add := mul (('+' | '-') mul)*
        // mul := unary (('*' | '/') unary)*
        // unary := '-' unary | atom
        // atom := num | ident | '(' expr ')' | '[' expr ']'

        public ulong ParseExpr() => ParseBitOr();

        private ulong ParseBitOr()
        {
            var v = ParseXor();
            while (_cur.Kind == TokKind.Or) { Eat(); v |= ParseXor(); }
            return v;
        }

        private ulong ParseXor()
        {
            var v = ParseBitAnd();
            while (_cur.Kind == TokKind.Xor) { Eat(); v ^= ParseBitAnd(); }
            return v;
        }

        private ulong ParseBitAnd()
        {
            var v = ParseShift();
            while (_cur.Kind == TokKind.And) { Eat(); v &= ParseShift(); }
            return v;
        }

        private ulong ParseShift()
        {
            var v = ParseAdd();
            while (_cur.Kind is TokKind.Shl or TokKind.Shr)
            {
                bool left = _cur.Kind == TokKind.Shl; Eat();
                int n = (int)(ParseAdd() & 0x3F);
                v = left ? v << n : v >> n;
            }
            return v;
        }

        private ulong ParseAdd()
        {
            var v = ParseMul();
            while (_cur.Kind is TokKind.Plus or TokKind.Minus)
            {
                bool minus = _cur.Kind == TokKind.Minus; Eat();
                var r = ParseMul();
                v = minus ? v - r : v + r;
            }
            return v;
        }

        private ulong ParseMul()
        {
            var v = ParseUnary();
            while (_cur.Kind is TokKind.Star or TokKind.Slash)
            {
                bool div = _cur.Kind == TokKind.Slash; Eat();
                var r = ParseUnary();
                v = div ? (r == 0 ? 0 : v / r) : v * r;
            }
            return v;
        }

        private ulong ParseUnary()
        {
            if (_cur.Kind == TokKind.Minus)
            {
                Eat();
                return (ulong)(-(long)ParseUnary());
            }
            return ParseAtom();
        }

        private ulong ParseAtom()
        {
            switch (_cur.Kind)
            {
                case TokKind.Num:
                    var n = _cur.Num; Eat(); return n;
                case TokKind.LParen:
                    Eat();
                    var v = ParseExpr();
                    if (_cur.Kind != TokKind.RParen) throw new FormatException("expected ')'");
                    Eat(); return v;
                case TokKind.LBracket:
                    Eat();
                    var a = ParseExpr();
                    if (_cur.Kind != TokKind.RBracket) throw new FormatException("expected ']'");
                    Eat();
                    return _outer.Deref(a);
                case TokKind.Ident:
                    var name = _cur.Text!; Eat();
                    return _outer.ResolveIdent(name);
                default:
                    throw new FormatException($"unexpected token {_cur.Kind}");
            }
        }
    }

    // ── Резолв идентификатора → регистр или символ ───────────────────────

    private ulong ResolveIdent(string name)
    {
        // Регистр (case-insensitive)
        var regs = _getRegs();
        if (regs.HasValue)
        {
            var v = GetReg(regs.Value, name.ToLowerInvariant());
            if (v.HasValue) return v.Value;
        }
        // module!symbol через dbghelp
        ulong sym = _syms.Lookup(name);
        if (sym != 0) return sym;
        throw new FormatException($"unknown identifier: {name}");
    }

    private static ulong? GetReg(KF_REGISTERS r, string name) => name switch
    {
        "rax" => r.Rax, "rbx" => r.Rbx, "rcx" => r.Rcx, "rdx" => r.Rdx,
        "rsi" => r.Rsi, "rdi" => r.Rdi, "rbp" => r.Rbp, "rsp" => r.Rsp,
        "r8"  => r.R8,  "r9"  => r.R9,  "r10" => r.R10, "r11" => r.R11,
        "r12" => r.R12, "r13" => r.R13, "r14" => r.R14, "r15" => r.R15,
        "rip" => r.Rip, "rflags" => r.Rflags,
        "eax" => r.Rax & 0xFFFFFFFF, "ebx" => r.Rbx & 0xFFFFFFFF,
        "ecx" => r.Rcx & 0xFFFFFFFF, "edx" => r.Rdx & 0xFFFFFFFF,
        "esi" => r.Rsi & 0xFFFFFFFF, "edi" => r.Rdi & 0xFFFFFFFF,
        "ebp" => r.Rbp & 0xFFFFFFFF, "esp" => r.Rsp & 0xFFFFFFFF,
        "eip" => r.Rip & 0xFFFFFFFF, "eflags" => r.Rflags & 0xFFFFFFFF,
        _ => null,
    };

    private ulong Deref(ulong addr)
    {
        uint pid = _getPid();
        if (pid == 0) throw new FormatException("memory deref requires target");
        uint size = _is32Bit() ? 4u : 8u;
        var data = _client.ReadMemory(pid, addr, size);
        if (data == null || data.Length < size) throw new FormatException($"read {size}@{addr:X} failed");
        return _is32Bit() ? BitConverter.ToUInt32(data, 0) : BitConverter.ToUInt64(data, 0);
    }
}
