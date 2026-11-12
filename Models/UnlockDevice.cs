using System.Collections.Generic;

namespace Mosytool.Models;

public sealed class UnlockDevice
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Model { get; init; }

    public required string Chip { get; init; }

    public required ushort HwCode { get; init; }

    public required string Method { get; init; }

    public required string Folder { get; init; }

    public required IReadOnlyList<string> Steps { get; init; }

    public string Display => $"{Name} · {Model}";
}
