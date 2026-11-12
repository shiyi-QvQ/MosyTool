using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace Mosytool.Models;

public class ExtentInfo
{
    public long Sectors      { get; set; }
    public int  TargetType   { get; set; }
    public long TargetData   { get; set; }
    public int  TargetSource { get; set; }

    public bool IsLinear => TargetType == 0;
    public bool IsZero   => TargetType == 1;
    public long ByteOffset => TargetData * 512;
    public long ByteLength => Sectors * 512;
}

public class LogicalPartition : INotifyPropertyChanged
{
    private bool   _isSelected;
    private string _imagePath = string.Empty;
    private long   _imageSize;

    public string Name         { get; set; } = "";
    public string Group        { get; set; } = "default";
    public bool   ReadOnly     { get; set; }
    public bool   SlotSuffixed { get; set; }
    public bool   Updated      { get; set; }
    public bool   Disabled     { get; set; }

    public long Size { get; set; }

    public long DataOffset { get; set; }

    public List<ExtentInfo> Extents { get; } = new();

    public string ImagePath
    {
        get => _imagePath;
        set
        {
            if (_imagePath == value) return;
            _imagePath = value;
            Raise(nameof(ImagePath));
            Raise(nameof(Display));
        }
    }

    public long ImageSize
    {
        get => _imageSize;
        set
        {
            if (_imageSize == value) return;
            _imageSize = value;
            Raise(nameof(ImageSize));
            Raise(nameof(SizeText));
            Raise(nameof(Display));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    public string SizeText => FormatSize(Size);

    public string Display => _imagePath.Length > 0
        ? $"{Name,-22} ← {Path.GetFileName(_imagePath)}  ({FormatSize(_imageSize)})"
        : Extents.Count > 0
            ? $"{Name,-22} {SizeText,10}  [{Group}]  {Extents.Count} 段"
            : $"{Name,-22} {SizeText,10}  [{Group}]";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
        if (bytes >= 1L << 20) return $"{bytes / 1024.0 / 1024.0:0.##} MB";
        if (bytes >= 1L << 10) return $"{bytes / 1024.0:0.##} KB";
        return $"{bytes} B";
    }
}
