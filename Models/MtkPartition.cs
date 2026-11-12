namespace Mosytool.Models;

public sealed class MtkPartition
{
    public string Name { get; init; } = string.Empty;

    public ulong Address { get; init; }

    public ulong Size { get; init; }

    public string SizeHuman { get; init; } = string.Empty;

    public string Section { get; init; } = string.Empty;

    public string AddressHex => $"0x{Address:X16}";

    public string SizeHex => $"0x{Size:X16}";
}
