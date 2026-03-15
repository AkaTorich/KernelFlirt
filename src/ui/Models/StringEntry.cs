namespace KernelFlirt.UI.Models;

public enum StringType { ASCII, Unicode }

public class StringEntry
{
    public int Index { get; set; }
    public string ModuleName { get; set; } = "";
    public string SectionName { get; set; } = "";
    public ulong Address { get; set; }
    public string Value { get; set; } = "";
    public StringType Type { get; set; }
    public int Length { get; set; }
    public bool HasBreakpoint { get; set; }
    public bool Is32Bit { get; set; }

    public string AddressHex => Is32Bit ? $"{Address:X8}" : $"{Address:X16}";
    public string TypeName => Type == StringType.Unicode ? "Unicode" : "ASCII";
}
