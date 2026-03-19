using System.Runtime.InteropServices;

namespace Magicmida;

public enum LogMsgType { Info, Good, Fatal }

public delegate void LogProc(LogMsgType msgType, string msg);

public static class Utils
{
    public static LogProc? Log;

    public static string AccessViolationFlagToStr(nuint flag)
    {
        return flag switch
        {
            0 => "Read",
            1 => "Write",
            8 => "Execute",
            _ => flag.ToString()
        };
    }

    /// <summary>
    /// Pattern search with wildcards ('?' chars). Pattern is hex string where '?' means wildcard byte.
    /// Returns offset from start of buffer, or 0 if not found.
    /// </summary>
    public static unsafe uint FindDynamic(string pattern, byte* buf, uint size)
    {
        int patLen = pattern.Length / 2;
        if (patLen == 0 || size < patLen) return 0;

        var bytes = new byte[patLen];
        uint wildcard = 0;

        for (int i = 0; i < patLen; i++)
        {
            char c1 = pattern[i * 2];
            char c2 = pattern[i * 2 + 1];
            if (c1 == '?')
                wildcard |= (1u << i);
            else
                bytes[i] = Convert.ToByte(new string(new[] { c1, c2 }), 16);
        }

        byte* start = buf;
        byte* max = buf + size - patLen;
        while (buf < max)
        {
            bool match = true;
            for (int j = 0; j < patLen; j++)
            {
                if (((wildcard >> j) & 1) == 0 && buf[j] != bytes[j])
                {
                    match = false;
                    break;
                }
                if (j == patLen - 1 && match)
                    return (uint)(buf - start);
            }
            buf++;
        }

        return 0;
    }

    /// <summary>
    /// Exact pattern search. Pattern is hex string (no wildcards).
    /// Returns offset from start of buffer, or 0 if not found.
    /// </summary>
    public static unsafe uint FindStatic(string pattern, byte* buf, uint size)
    {
        int patLen = pattern.Length / 2;
        if (patLen == 0 || size < patLen) return 0;

        var bytes = new byte[patLen];
        for (int i = 0; i < patLen; i++)
            bytes[i] = Convert.ToByte(pattern.Substring(i * 2, 2), 16);

        byte* start = buf;
        byte* max = buf + size - patLen;
        while (buf < max)
        {
            bool match = true;
            for (int j = 0; j < patLen; j++)
            {
                if (buf[j] != bytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return (uint)(buf - start);
            buf++;
        }

        return 0;
    }

    public static uint GetWindowsBuildNumber()
    {
        var info = new NativeApi.OSVERSIONINFOW();
        info.dwOSVersionInfoSize = (uint)Marshal.SizeOf<NativeApi.OSVERSIONINFOW>();
        NativeApi.RtlGetVersion(ref info);
        return info.dwBuildNumber;
    }

    public static unsafe uint GetPETimestamp(string filename)
    {
        var header = new byte[0x1000];
        using var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Read(header, 0, header.Length);

        fixed (byte* p = header)
        {
            var dos = (NativeApi.IMAGE_DOS_HEADER*)p;
            if ((uint)dos->e_lfanew > 0xF00)
                return 0;
            var nt = (NativeApi.IMAGE_NT_HEADERS32*)(p + dos->e_lfanew);
            return nt->FileHeader.TimeDateStamp;
        }
    }
}

public struct MemoryRegion
{
    public nuint Address;
    public uint Size;

    public MemoryRegion(nuint address, uint size) { Address = address; Size = size; }

    public bool Contains(nuint addr) => addr >= Address && addr < Address + Size;
}
