using System.ComponentModel;

namespace Mosytool.Models;

public class PayloadPartition : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name          { get; set; } = "";
    public long   Size          { get; set; }
    public long   DataOffset    { get; set; }
    public long   DataLength    { get; set; }
    public int    OperationType { get; set; }
    public string TypeName      { get; set; } = "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string SizeText => FormatSize(Size);

    public string Display => $"{Name}    {SizeText}    [{TypeName}]";

    public bool IsExtractable => OperationType is 0 or 1 or 8;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB";
        if (bytes >= 1L << 20) return $"{bytes / 1024.0 / 1024.0:0.##} MB";
        if (bytes >= 1L << 10) return $"{bytes / 1024.0:0.##} KB";
        return $"{bytes} B";
    }
}
