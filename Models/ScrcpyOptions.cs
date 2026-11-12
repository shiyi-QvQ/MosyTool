namespace Mosytool.Models;

public sealed class ScrcpyOptions
{
    public int MaxSize { get; set; }

    public string VideoBitRate { get; set; } = "8M";

    public int MaxFps { get; set; } = 60;

    public bool NoAudio { get; set; } = true;

    public bool TurnScreenOff { get; set; }

    public bool StayAwake { get; set; } = true;

    public bool AlwaysOnTop { get; set; }

    public bool Borderless { get; set; }

    public bool Fullscreen { get; set; }

    public string WindowTitle { get; set; } = "MosyTool 投屏";

    public string? RecordPath { get; set; }

    public string ExtraArguments { get; set; } = string.Empty;
}
