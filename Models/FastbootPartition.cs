using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mosytool.Models;

public sealed class FastbootPartition : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = string.Empty;

    public string BaseName { get; init; } = string.Empty;

    public string Slot { get; init; } = string.Empty;

    public bool IsActive { get; init; }

    public bool IsSlotted { get; init; }

    public string SlotInfo { get; init; } = string.Empty;

    public ulong Size { get; init; }

    public string SizeHuman { get; init; } = "—";

    public string SizeHex => Size > 0 ? $"0x{Size:X}" : "—";

    public string Type { get; init; } = string.Empty;

    public string SlotDisplay => Slot.Length == 0
        ? "—"
        : IsActive ? $"{Slot}（当前）" : Slot;

    /// <summary>分区表里的勾选状态，由界面上的复选框双向绑定。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 是否属于「用户数据」这一类分区。刷机时这类分区通常要单独勾选，
    /// 与系统分区区分开（数据会丢，但设备能正常启动）。
    /// </summary>
    public bool IsUserData
    {
        get
        {
            var key = (BaseName.Length > 0 ? BaseName : Name).ToLowerInvariant();
            return key is "userdata" or "data" or "cache" or "metadata" or "persist"
                or "mdball" or "dalvik-cache" or "internal_sd" or "storage";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
