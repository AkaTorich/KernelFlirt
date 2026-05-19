// ANSI escape-цвета для дизасма и hex-dump. Включаются автоматически при запуске
// в Windows Terminal / любой консоли с поддержкой VT100. Можно выключить
// глобально через `color off`.
using System.Runtime.InteropServices;

namespace KernelFlirt.Cli;

internal static class Ansi
{
    public static bool Enabled { get; set; } = true;

    // Базовые палитры (xterm-256 близкие к VS Code Dark+ / x64dbg)
    public const string Reset    = "\x1b[0m";
    public const string Dim      = "\x1b[2m";
    public const string Bold     = "\x1b[1m";

    public const string Gray     = "\x1b[38;5;245m";   // адреса, разделители
    public const string Blue     = "\x1b[38;5;75m";    // мнемоники
    public const string Magenta  = "\x1b[38;5;176m";   // control-flow (jmp/jcc/call/ret)
    public const string Yellow   = "\x1b[38;5;221m";   // целевые символы
    public const string Green    = "\x1b[38;5;108m";   // strings / комментарии
    public const string Cyan     = "\x1b[38;5;110m";   // регистры
    public const string Orange   = "\x1b[38;5;215m";   // immediate / hex
    public const string Red      = "\x1b[38;5;203m";   // BP / fault
    public const string White    = "\x1b[38;5;253m";

    public static string Wrap(string color, string text)
        => Enabled ? color + text + Reset : text;

    // Инициализация VT-режима на старых консолях (cmd.exe). Windows Terminal,
    // Powershell 7+ и сам Windows 10+ всё поддерживают по умолчанию.
    public static void EnableVtOnLegacyConsole()
    {
        try
        {
            var h = GetStdHandle(STD_OUTPUT_HANDLE);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return;
            if (!GetConsoleMode(h, out uint mode)) return;
            const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
            if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) == 0)
                SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch { /* консоль другого типа — пропускаем */ }
    }

    private const int STD_OUTPUT_HANDLE = -11;
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    /// <summary>
    /// Грубый колоризатор строки от NasmFormatter: подсвечивает мнемонику,
    /// регистры, immediate. Не AST — простой regex-уровень. Этого достаточно
    /// чтобы дизасм перестал быть стеной серого текста.
    /// </summary>
    public static string ColorizeAsmLine(string text)
    {
        if (!Enabled) return text;
        // Разделим на opcode + operands по первому пробелу/табу.
        int sp = text.IndexOfAny(new[] { ' ', '\t' });
        string op, operands;
        if (sp < 0) { op = text; operands = ""; }
        else        { op = text[..sp]; operands = text[sp..]; }

        string opColor = IsControlFlow(op) ? Magenta : Blue;
        var sb = new System.Text.StringBuilder();
        sb.Append(opColor).Append(op).Append(Reset);
        if (operands.Length > 0)
        {
            // Простое подсветка: регистры [a-z][a-z\d]{1,3} → cyan,
            // hex (0x... или /[0-9a-f]+h?/) → orange, остальное серым.
            sb.Append(ColorizeOperands(operands));
        }
        return sb.ToString();
    }

    private static bool IsControlFlow(string op)
    {
        op = op.ToLowerInvariant();
        return op is "jmp" or "call" or "ret" or "retf" or "retn"
            or "iret" or "iretd" or "iretq" or "int" or "int3" or "ud2"
            or "loop" or "loope" or "loopne"
            || (op.StartsWith("j") && op != "jmpe");
    }

    private static readonly HashSet<string> RegisterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "rax","rbx","rcx","rdx","rsi","rdi","rbp","rsp","rip",
        "r8","r9","r10","r11","r12","r13","r14","r15",
        "eax","ebx","ecx","edx","esi","edi","ebp","esp","eip",
        "ax","bx","cx","dx","si","di","bp","sp",
        "al","bl","cl","dl","ah","bh","ch","dh",
        "r8d","r9d","r10d","r11d","r12d","r13d","r14d","r15d",
        "r8w","r9w","r10w","r11w","r12w","r13w","r14w","r15w",
        "r8b","r9b","r10b","r11b","r12b","r13b","r14b","r15b",
        "cs","ds","es","fs","gs","ss",
    };

    private static string ColorizeOperands(string ops)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < ops.Length)
        {
            char c = ops[i];
            // Идентификатор: буква + [a-z\d]
            if (char.IsLetter(c))
            {
                int start = i;
                while (i < ops.Length && (char.IsLetterOrDigit(ops[i]))) i++;
                var tok = ops[start..i];
                if (RegisterNames.Contains(tok))
                    sb.Append(Cyan).Append(tok).Append(Reset);
                else
                    sb.Append(tok);
            }
            else if (c == '0' && i + 1 < ops.Length && (ops[i + 1] == 'x' || ops[i + 1] == 'X'))
            {
                int start = i; i += 2;
                while (i < ops.Length && IsHex(ops[i])) i++;
                sb.Append(Orange).Append(ops[start..i]).Append(Reset);
            }
            else if (char.IsDigit(c))
            {
                int start = i;
                while (i < ops.Length && (IsHex(ops[i]) || ops[i] == 'h')) i++;
                sb.Append(Orange).Append(ops[start..i]).Append(Reset);
            }
            else
            {
                sb.Append(c); i++;
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
