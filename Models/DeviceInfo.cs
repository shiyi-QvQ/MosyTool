namespace Mosytool.Models;

public class DeviceInfo
{
    public string Name            { get; set; } = "-";
    public string Codename        { get; set; } = "-";
    public string Serial          { get; set; } = "-";
    public string State           { get; set; } = "未连接";
    public string StateKind       { get; set; } = "disconnected";
    public string AndroidVersion  { get; set; } = "-";
    public string Hardware        { get; set; } = "-";
    public string Cpu             { get; set; } = "-";
    public string Resolution      { get; set; } = "-";
    public string Battery         { get; set; } = "-";
    public string InternalStorage { get; set; } = "-";
    public string TotalRam        { get; set; } = "-";
    public string AvailableRam    { get; set; } = "-";

    public string RomVersion   { get; set; } = "-";
    public string KernelVersion{ get; set; } = "-";
    public string BuildNumber  { get; set; } = "-";

    public int  BatteryPercent  { get; set; } = -1;
    public bool BatteryCharging { get; set; }

    public double StorageTotalGb { get; set; }
    public double StorageUsedGb  { get; set; }
    public double StorageFreeGb  { get; set; }

    public string StorageNominal { get; set; } = string.Empty;

    public double StoragePercent =>
        StorageTotalGb > 0 ? StorageUsedGb / StorageTotalGb * 100.0 : 0.0;

    public double RamTotalGb { get; set; }
    public double RamAvailGb { get; set; }

    public double RamUsedGb => Math.Max(0, RamTotalGb - RamAvailGb);

    public string RamNominal { get; set; } = string.Empty;

    public double RamPercent =>
        RamTotalGb > 0 ? RamUsedGb / RamTotalGb * 100.0 : 0.0;

    public bool? BootloaderUnlocked { get; set; }
}
