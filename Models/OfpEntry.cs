using System.ComponentModel;

namespace Mosytool.Models;

public class OfpEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";

    public string FileName { get; set; } = "";

    public string Group { get; set; } = "";

    public long Start      { get; set; }
    public long Length     { get; set; }
    public long RawLength  { get; set; }

    public bool Encrypted  { get; set; }

    public long DecryptSize { get; set; }

    public string Md5    { get; set; } = "";
    public string Sha256 { get; set; } = "";

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

    public string SizeText => Services.SuperService.FormatSize(Length);

    public string Display =>
        $"{FileName,-34} {SizeText,10}  {Group}{(Encrypted ? "" : "  (原样)")}";

    public event PropertyChangedEventHandler? PropertyChanged;
}
