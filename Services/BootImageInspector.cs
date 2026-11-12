using System;
using System.IO;
using System.Text;

namespace Mosytool.Services;

public enum KernelPresence
{
    /// <summary>不是标准 Android boot 头，判断不了。</summary>
    Unknown,

    /// <summary>镜像里有 kernel（boot.img）。</summary>
    Present,

    /// <summary>镜像里没有 kernel（init_boot / vendor_boot）。</summary>
    Absent,
}

/// <summary>镜像是哪一类启动件——决定了 KernelSU 系该走 GKI 还是 LKM。</summary>
public enum BootImageKind
{
    Unknown,

    /// <summary>boot.img：可能有内核，KernelSU 系首选 GKI。</summary>
    Boot,

    /// <summary>init_boot.img（Android 13+）：只有 ramdisk，只能走 LKM。</summary>
    InitBoot,

    /// <summary>vendor_boot.img：只有 vendor ramdisk 与 dtb，只能走 LKM。</summary>
    VendorBoot,
}

/// <summary>
/// 只看文件头就把 boot 镜像的家底摸清楚，不用解包。
///
/// 判断「有没有内核」是选修补路线的关键：KernelSU 系内核补丁（kpimg）必须改 kernel，
/// 而 Android 13 之后内核以外的启动件挪到了 init_boot.img，那个文件里 kernel_size 恒为 0。
/// 选错路线的话 kptools 会解出一个空 kernel，再往下全是看不懂的报错。
/// </summary>
public static class BootImageInspector
{
    private const int HeaderProbeLength = 44;

    /// <summary>
    /// 读镜像头判断内核是否存在。
    /// v0–v4 的 boot 头都是 magic[8] + kernel_size(u32)@8 + … + header_version(u32)@40，位置一致；
    /// vendor_boot 是另一套 magic，本来就不含内核。
    /// </summary>
    public static KernelPresence ProbeKernel(string imagePath, out long kernelSize, out int headerVersion)
    {
        kernelSize    = 0;
        headerVersion = -1;

        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var head = new byte[HeaderProbeLength];
            if (fs.Read(head, 0, HeaderProbeLength) < HeaderProbeLength)
                return KernelPresence.Unknown;

            var magic = Encoding.ASCII.GetString(head, 0, 8);

            // vendor_boot 里放的是 vendor ramdisk 和 dtb，没有内核
            if (magic == "VNDRBOOT")
                return KernelPresence.Absent;

            // 三星等厂商的私有格式，交给解包工具自己判断，别在这儿瞎猜
            if (magic != "ANDROID!")
                return KernelPresence.Unknown;

            kernelSize    = BitConverter.ToUInt32(head, 8);
            headerVersion = (int)BitConverter.ToUInt32(head, 40);

            return kernelSize > 0 ? KernelPresence.Present : KernelPresence.Absent;
        }
        catch
        {
            return KernelPresence.Unknown;
        }
    }

    /// <summary>
    /// 一次摸清镜像的家底：类型 + 有没有内核 + 头版本。
    /// 界面靠这个决定 KernelSU 系该走 GKI（改内核，要安装包里的 kpimg）
    /// 还是 LKM（注 ramdisk，用预编译 .ko，不要安装包）。
    /// </summary>
    public static BootImageInfo Probe(string imagePath)
    {
        var presence = ProbeKernel(imagePath, out var kernelSize, out var headerVersion);
        var kind     = Classify(imagePath, presence);

        return new BootImageInfo(kind, presence, kernelSize, headerVersion);
    }

    private static BootImageKind Classify(string imagePath, KernelPresence presence)
    {
        try
        {
            using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var magicBytes = new byte[8];
            if (fs.Read(magicBytes, 0, 8) < 8) return BootImageKind.Unknown;

            var magic = Encoding.ASCII.GetString(magicBytes);

            if (magic == "VNDRBOOT") return BootImageKind.VendorBoot;
            if (magic != "ANDROID!") return BootImageKind.Unknown;

            // 同一个头、kernel_size 为 0 的只可能是 init_boot（boot.img 不带内核没有意义）
            return presence == KernelPresence.Present ? BootImageKind.Boot : BootImageKind.InitBoot;
        }
        catch
        {
            return BootImageKind.Unknown;
        }
    }
}

/// <summary>镜像探测结果。</summary>
public readonly record struct BootImageInfo(BootImageKind Kind,
                                           KernelPresence Kernel,
                                           long KernelSize,
                                           int HeaderVersion)
{
    /// <summary>解包之前就能确定「KernelSU 系内核补丁」这条路走不走得通。</summary>
    public bool KernelPatchable => Kernel == KernelPresence.Present;

    /// <summary>给界面与日志用的一句话结论。</summary>
    public string Describe()
    {
        var name = Kind switch
        {
            BootImageKind.Boot       => "boot.img",
            BootImageKind.InitBoot   => "init_boot.img",
            BootImageKind.VendorBoot => "vendor_boot.img",
            _                        => "未知类型镜像",
        };

        if (Kind == BootImageKind.Unknown)
            return "无法从文件头判断镜像类型，将交给解包工具识别。";

        var kernel = Kind == BootImageKind.Boot
            ? $"内核 {KernelSize / 1024.0 / 1024.0:F1} MB"
            : "无内核";

        return $"{name}（header v{HeaderVersion}，{kernel}）";
    }
}
