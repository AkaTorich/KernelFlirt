// Лёгкая обёртка над Iced.Intel для дизассемблирования и форматирования.
using Iced.Intel;

namespace KernelFlirt.Cli;

/// <summary>Анализ одной инструкции — нужен Step Over / Step Out.</summary>
internal readonly record struct InsnInfo(
    ulong  Address,
    int    Length,
    ulong  NextAddress,
    string Mnemonic,
    FlowControl Flow,
    ulong? BranchTarget  // null если нет / не вычислимо (например indirect)
);

internal static class Disasm
{
    /// <summary>Декодирует ОДНУ инструкцию по адресу — для Step Over/Out.</summary>
    public static InsnInfo? DecodeOne(byte[] data, ulong baseAddr, bool is32Bit = false)
    {
        var reader = new ByteArrayCodeReader(data);
        var decoder = Decoder.Create(is32Bit ? 32 : 64, reader);
        decoder.IP = baseAddr;
        decoder.Decode(out var insn);
        if (insn.IsInvalid || insn.Length == 0) return null;

        ulong? target = null;
        var fc = insn.FlowControl;
        if (fc is FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
            or FlowControl.Call)
        {
            // Прямая ветвь: near или far. Берём near-branch target, fallback на 64-битный.
            if (insn.NearBranchTarget != 0)         target = insn.NearBranchTarget;
            else if (insn.NearBranch32 != 0)        target = insn.NearBranch32;
            else if (insn.NearBranch64 != 0)        target = insn.NearBranch64;
            // Indirect ветви (call qword ptr [rax]) — target неизвестен статически.
        }

        return new InsnInfo(
            Address: insn.IP,
            Length: insn.Length,
            NextAddress: insn.IP + (ulong)insn.Length,
            Mnemonic: insn.Mnemonic.ToString().ToLowerInvariant(),
            Flow: fc,
            BranchTarget: target
        );
    }

    /// <summary>Запись одной инструкции с branch-info для аннотации.</summary>
    internal readonly record struct DisasmLine(
        ulong Addr, byte[] Bytes, string Text, ulong? BranchTarget, FlowControl Flow);

    /// <summary>
    /// Дизассемблирует <paramref name="data"/>, начиная с адреса <paramref name="baseAddr"/>,
    /// возвращает указанное количество инструкций (или меньше, если данные кончатся).
    /// </summary>
    public static List<DisasmLine> Decode(
        byte[] data, ulong baseAddr, int count, bool is32Bit = false)
    {
        var result = new List<DisasmLine>();
        var reader = new ByteArrayCodeReader(data);
        var decoder = Decoder.Create(is32Bit ? 32 : 64, reader);
        decoder.IP = baseAddr;

        var formatter = new NasmFormatter();
        formatter.Options.DigitSeparator = "";
        formatter.Options.FirstOperandCharIndex = 8;
        formatter.Options.HexPrefix = "0x";
        formatter.Options.HexSuffix = "";
        formatter.Options.UppercaseHex = false;

        var output = new StringOutput();

        for (int i = 0; i < count && decoder.IP < baseAddr + (ulong)data.Length; i++)
        {
            decoder.Decode(out var insn);
            output.Reset();
            formatter.Format(insn, output);
            var text = output.ToStringAndReset();

            int insnLen = insn.Length;
            if (insnLen <= 0) break;
            int offset = (int)(insn.IP - baseAddr);
            if (offset + insnLen > data.Length) break;

            ulong? target = null;
            var fc = insn.FlowControl;
            if (fc is FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
                or FlowControl.Call)
            {
                if (insn.NearBranchTarget != 0)   target = insn.NearBranchTarget;
                else if (insn.NearBranch64 != 0)  target = insn.NearBranch64;
                else if (insn.NearBranch32 != 0)  target = insn.NearBranch32;
            }

            var bytes = new byte[insnLen];
            Buffer.BlockCopy(data, offset, bytes, 0, insnLen);
            result.Add(new DisasmLine(insn.IP, bytes, text, target, fc));
        }
        return result;
    }
}
