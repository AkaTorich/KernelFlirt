namespace KernelFlirt.UI.Models;

public class Patch
{
    public ulong Address { get; set; }
    public byte[] OriginalBytes { get; set; } = [];
    public byte[] PatchedBytes { get; set; }  = [];
    public string? ModuleName { get; set; }
    public string AddressHex => $"{Address:X16}";
    public string OriginalHex => BitConverter.ToString(OriginalBytes).Replace("-", " ");
    public string PatchedHex => BitConverter.ToString(PatchedBytes).Replace("-", " ");
    public int Size => PatchedBytes.Length;
}
