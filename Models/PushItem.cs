using System.ComponentModel;
using System.IO;

namespace Mosytool.Models;

public sealed class PushItem : INotifyPropertyChanged
{
    public PushItem(string localPath) => LocalPath = localPath.Trim();

    public string LocalPath { get; }

    public string Name
    {
        get
        {
            var trimmed = LocalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name    = Path.GetFileName(trimmed);
            return name.Length > 0 ? name : LocalPath;
        }
    }

    public string KindLabel => Directory.Exists(LocalPath) ? "文件夹" : "文件";

    public string SizeHuman
    {
        get
        {
            if (Directory.Exists(LocalPath)) return "—";

            try
            {
                return FastbootSize.Format(new FileInfo(LocalPath).Length);
            }
            catch
            {
                return "—";
            }
        }
    }

    private string _status = "待推送";

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal static class FastbootSize
{
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double   value = bytes;
        int      unit  = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.00} {units[unit]}";
    }
}
