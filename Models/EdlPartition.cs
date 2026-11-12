namespace Mosytool.Models;

public sealed class EdlPartition
{
    public int Index { get; init; }

    public string Name { get; init; } = string.Empty;

    public string TypeGuid { get; init; } = string.Empty;

    public string PartitionGuid { get; init; } = string.Empty;

    public ulong FirstLba { get; init; }

    public ulong LastLba { get; init; }

    public ulong Sectors { get; init; }

    public ulong Size { get; init; }

    public string SizeHuman { get; init; } = string.Empty;

    public string FirstLbaHex => $"0x{FirstLba:X}";

    public string LastLbaHex => $"0x{LastLba:X}";

    public string SizeHex => $"0x{Size:X}";

    public EdlPartition WithSizeHuman(string human) => new()
    {
        Index         = Index,
        Name          = Name,
        TypeGuid      = TypeGuid,
        PartitionGuid = PartitionGuid,
        FirstLba      = FirstLba,
        LastLba       = LastLba,
        Sectors       = Sectors,
        Size          = Size,
        SizeHuman     = human,
    };
}
