using System;
using System.ComponentModel;
using System.IO;
using Wpf.Ui.Controls;

namespace Mosytool.Models;

public sealed class AdbFileItem : INotifyPropertyChanged
{
    public required string Name { get; init; }

    public bool IsDirectory { get; init; }

    public bool IsLocal { get; init; }

    public long Size { get; init; }

    public string LocalPath { get; init; } = string.Empty;

    public string KindLabel => IsDirectory ? "文件夹" : IsLocal ? "本机文件" : "文件";

    public string SizeHuman => IsDirectory ? "—" : FastbootSize.Format(Size);

    public SymbolRegular Icon => IsDirectory
        ? SymbolRegular.Folder24
        : IsLocal
            ? SymbolRegular.ArrowUpload24
            : SymbolRegular.Document24;

    private string _status = string.Empty;

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

    public static AdbFileItem FromDevice(string name, bool isDirectory, long size)
        => new() { Name = name, IsDirectory = isDirectory, Size = size };

    public static AdbFileItem FromLocal(string localPath)
    {
        var trimmed = localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name    = Path.GetFileName(trimmed);
        var isDir   = Directory.Exists(localPath);

        long size = 0;
        if (!isDir)
        {
            try { size = new FileInfo(localPath).Length; }
            catch { size = 0; }
        }

        return new AdbFileItem
        {
            Name        = name.Length > 0 ? name : localPath,
            IsDirectory = isDir,
            IsLocal     = true,
            Size        = size,
            LocalPath   = localPath,
        };
    }
}
