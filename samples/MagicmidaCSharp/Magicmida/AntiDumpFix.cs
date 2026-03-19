using System.Runtime.InteropServices;
using static Magicmida.NativeApi;

namespace Magicmida;

/// <summary>
/// Fixes one type of anti-dump that checks the PE header's AddressOfEntryPoint field.
/// Installs a shim at OEP that restores the original entrypoint before jumping to the VM.
/// </summary>
public class AntiDumpFixer
{
    private readonly IntPtr _hProcess;
    private readonly nuint _imageBase;

    public AntiDumpFixer(IntPtr hProcess, nuint imageBase)
    {
        _hProcess = hProcess;
        _imageBase = imageBase;
    }

    private bool RPM(nuint address, IntPtr buf, nuint size)
    {
        return ReadProcessMemory(_hProcess, (IntPtr)(nint)address, buf, size, out _);
    }

    public unsafe void RedirectOEP(nuint oep, nuint iat)
    {
        byte[] pushArgsRW = { 0x6A, 0x00, 0x54, 0x6A, 0x04, 0x68, 0x00, 0x04, 0x00, 0x00, 0x68 };
        byte[] pushArgsOldProt = { 0x54, 0xFF, 0x74, 0x24, 0x04, 0x68, 0x00, 0x04, 0x00, 0x00, 0x68 };

        uint displ = 0;
        RPM(oep + 1, (IntPtr)(&displ), 4);

        var vProtectAddr = (nuint)(nint)GetProcAddress(GetModuleHandle("kernel32.dll"), "VirtualProtect");

        uint vProtectIAT = 0;
        var iatData = new nuint[512];
        fixed (nuint* pi = iatData)
            RPM(iat, (IntPtr)pi, (nuint)(512 * IntPtr.Size));

        for (int i = 0; i < iatData.Length; i++)
        {
            if (iatData[i] == vProtectAddr)
            {
                vProtectIAT = (uint)(iat + (nuint)(i * 4));
                break;
            }
        }

        if (vProtectIAT == 0)
        {
            Utils.Log?.Invoke(LogMsgType.Fatal, "VirtualProtect not found in IAT");
            return;
        }

        // Build shellcode
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // VirtualProtect(ImageBase, $400, PAGE_READWRITE, OldProtect)
        bw.Write(pushArgsRW);
        bw.Write((uint)_imageBase);
        bw.Write((ushort)0x15FF); // call dword ptr
        bw.Write(vProtectIAT);

        // mov dword ptr [OptHdr.EP], ThemidaEntrypoint
        bw.Write((ushort)0x05C7);
        uint lfaNew = 0;
        RPM(_imageBase + 0x3C, (IntPtr)(&lfaNew), 4);
        uint originalEP = 0;
        if (!RPM(_imageBase + lfaNew + 0x28, (IntPtr)(&originalEP), 4))
        {
            Utils.Log?.Invoke(LogMsgType.Fatal, "ReadProcessMemory failed");
            return;
        }
        bw.Write((uint)(_imageBase + lfaNew + 0x28));
        bw.Write(originalEP);

        // VirtualProtect(ImageBase, $400, OldProtect, _)
        bw.Write(pushArgsOldProt);
        bw.Write((uint)_imageBase);
        bw.Write((ushort)0x15FF);
        bw.Write(vProtectIAT);
        bw.Write((byte)0x58); // pop eax

        // jmp vm
        bw.Write((byte)0xE9);
        uint codeSize = (uint)ms.Position + 4;
        bw.Write(displ - (codeSize - 5));

        var code = ms.ToArray();
        fixed (byte* pCode = code)
        {
            if (WriteProcessMemory(_hProcess, (IntPtr)(nint)oep, (IntPtr)pCode, (nuint)code.Length, out _))
            {
                Utils.Log?.Invoke(LogMsgType.Good, "Installed VM anti-dump (PE header) mitigation at OEP");
                Utils.Log?.Invoke(LogMsgType.Info, "NOTE: We assume there is enough space at the entrypoint, which may not be the case in every binary.");
            }
            else
                Utils.Log?.Invoke(LogMsgType.Fatal, "WriteProcessMemory failed");
        }
    }
}
