using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mosytool.Services;

/// <summary>
/// 小米官方线刷包（fastboot 包）的解析与执行。
///
/// 小米的 fastboot 包解压后是 <c>images\</c> 一堆 img 加几个脚本：
/// <c>flash_all.bat</c>（清空数据）、<c>flash_all_except_storage.bat</c>（保留数据）、
/// <c>flash_all_lock.bat</c>（清空数据并回锁），Linux 侧另有一份同名的 .sh。
///
/// 官方做法是把整包交给脚本自己跑，但那样界面看不到进度、也没法中途停。
/// 这里的做法是：只把脚本里的 fastboot 命令**读出来**，展开变量、解析成结构化步骤，
/// 再由本程序逐条调用 fastboot 执行。脚本本身从不被执行。
/// </summary>
public static class XiaomiFlashService
{
    // ==================== 数据模型 ====================

    /// <summary>步骤类型。决定界面上怎么标注、以及要不要检查镜像文件存在。</summary>
    public enum StepKind
    {
        /// <summary>fastboot flash &lt;分区&gt; &lt;镜像&gt;</summary>
        Flash,

        /// <summary>fastboot erase &lt;分区&gt;</summary>
        Erase,

        /// <summary>fastboot format &lt;分区&gt;</summary>
        Format,

        /// <summary>fastboot -w</summary>
        Wipe,

        /// <summary>fastboot reboot 系列</summary>
        Reboot,

        /// <summary>其它原样透传（set_active / oem lock / resize-logical-partition 等）</summary>
        Raw,
    }

    /// <summary>一条待执行的 fastboot 命令。</summary>
    public sealed class FlashStep
    {
        /// <summary>从 1 开始的序号，界面上按这个显示。</summary>
        public int Index { get; init; }

        public StepKind Kind { get; init; }

        /// <summary>原始动词（flash / erase / reboot …），用于界面归类。</summary>
        public string Verb { get; init; } = string.Empty;

        /// <summary>目标分区名，非分区类命令为空。</summary>
        public string Partition { get; init; } = string.Empty;

        /// <summary>镜像绝对路径（仅 Flash 有），已校验存在性。</summary>
        public string? ImagePath { get; init; }

        /// <summary>交给 fastboot.exe 的完整参数串（已展开变量、已加引号）。</summary>
        public string Arguments { get; init; } = string.Empty;

        /// <summary>界面上的说明文字，例如「刷入 boot」。</summary>
        public string Note { get; init; } = string.Empty;

        /// <summary>脚本里带 if exist 条件的步骤。</summary>
        public bool IsConditional { get; init; }

        /// <summary>镜像文件大小（字节），读不到为 0。</summary>
        public long ImageSize { get; init; }

        public string ImageSizeText => ImageSize <= 0 ? "—" : FastbootService.FormatSize((ulong)ImageSize);

        public string KindText => Kind switch
        {
            StepKind.Flash  => "刷入",
            StepKind.Erase  => "擦除",
            StepKind.Format => "格式化",
            StepKind.Wipe   => "清数据",
            StepKind.Reboot => "重启",
            _               => "命令",
        };

        /// <summary>命令预览，DataGrid 里直接读这个。</summary>
        public string CommandText => $"fastboot {Arguments}";
    }

    /// <summary>一个脚本解析出来的完整刷写计划。</summary>
    public sealed class FlashPlan
    {
        public string RomRoot { get; init; } = string.Empty;
        public string ScriptPath { get; init; } = string.Empty;
        public string ScriptName { get; init; } = string.Empty;

        /// <summary>「全量刷写：清空全部数据」这类可读模式名。</summary>
        public string ModeLabel { get; init; } = string.Empty;

        /// <summary>脚本里的模式标记，界面上用来提示风险。</summary>
        public bool KeepsUserData { get; init; }

        public bool Relocks { get; init; }

        public IReadOnlyList<FlashStep> Steps { get; init; } = Array.Empty<FlashStep>();

        /// <summary>解析期间跳过的内容（镜像缺失、条件不成立等），界面直接展示。</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public int FlashCount => Steps.Count(s => s.Kind == StepKind.Flash);
        public int WipeCount  => Steps.Count(s => s.Kind is StepKind.Erase or StepKind.Format or StepKind.Wipe);
        public long TotalBytes => Steps.Where(s => s.Kind == StepKind.Flash).Sum(s => s.ImageSize);
    }

    /// <summary>包里的一个可刷写脚本。</summary>
    public sealed record RomScript(string Name, string Path, string Label)
    {
        /// <summary>下拉框里显示的文字。</summary>
        public string Display => $"{Label}（{Name}）";
    }

    /// <summary>执行进度。</summary>
    public sealed record FlashProgress(int Index, int Total, string Text);

    /// <summary>执行结果。</summary>
    public sealed record FlashResult(bool Ok, int Done, int Failed, int Skipped,
                                     IReadOnlyList<string> Failures, bool Aborted);

    // ==================== 找包 / 找脚本 ====================

    /// <summary>
    /// 从用户选的目录里定位真正的包根目录。
    /// 解压出来常在外面多套一层（甚至两层）目录，所以往下一共探三层。
    /// 找不到脚本返回 null。
    /// </summary>
    public static string? ResolveRomRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;

        for (var depth = 0; depth <= 2; depth++)
            if (FindScriptsAt(path, depth).Count > 0) return path;

        return null;
    }

    /// <summary>列出包里的可刷写脚本，.bat 优先于同名的 .sh。</summary>
    public static List<RomScript> FindScripts(string romRoot)
    {
        for (var depth = 0; depth <= 2; depth++)
        {
            var found = FindScriptsAt(romRoot, depth);
            if (found.Count > 0) return found;
        }
        return new List<RomScript>();
    }

    private static List<RomScript> FindScriptsAt(string root, int depth)
    {
        // 同一个基名可能同时有 .bat 和 .sh，只留 .bat（Windows 上跑的就是它）
        var byBase = new Dictionary<string, RomScript>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in FilesAtDepth(root, depth))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith("flash_all", StringComparison.OrdinalIgnoreCase)) continue;

            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not (".bat" or ".sh")) continue;

            var baseName = Path.GetFileNameWithoutExtension(name);
            var isShell  = ext == ".sh";

            // 只有「现有的是 .sh、新来的也是 .bat」才顶替，其余情况先到的胜出
            if (byBase.TryGetValue(baseName, out var existing))
            {
                var existingIsShell = existing.Name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
                if (!(existingIsShell && !isShell)) continue;
            }

            byBase[baseName] = new RomScript(name, file, DescribeMode(baseName));
        }

        return byBase.Values
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把 flash_all_except_storage 这类文件名翻译成人话。</summary>
    private static string DescribeMode(string baseName)
    {
        var n = baseName.ToLowerInvariant();

        var keepsData = n.Contains("except_storage") || n.Contains("except_data_storage")
                     || n.Contains("except-userdata") || n.Contains("except_userdata");
        var relocks   = n.Contains("lock");

        var label = keepsData ? "保留用户数据" : "清空全部数据";
        if (relocks) label += "，并重新回锁";

        return $"全量刷写：{label}";
    }

    /// <summary>取 root 下第 depth 层目录里的所有文件。目录读不到就返回空，不抛。</summary>
    private static List<string> FilesAtDepth(string root, int depth)
    {
        var result = new List<string>();

        if (depth == 0)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root))
                    result.Add(file);
            }
            catch { }
            return result;
        }

        List<string> subs;
        try { subs = Directory.EnumerateDirectories(root).ToList(); }
        catch { return result; }

        foreach (var sub in subs)
            result.AddRange(FilesAtDepth(sub, depth - 1));

        return result;
    }

    // ==================== 解析 ====================

    /// <summary>解析指定脚本，得到可执行的步骤清单。</summary>
    public static FlashPlan Parse(string scriptPath)
    {
        var romRoot  = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? string.Empty;
        var name     = Path.GetFileName(scriptPath);
        var baseName = Path.GetFileNameWithoutExtension(scriptPath);
        var isShell  = Path.GetExtension(scriptPath).Equals(".sh", StringComparison.OrdinalIgnoreCase);

        var steps    = new List<FlashStep>();
        var warnings = new List<string>();

        // 脚本自己 set 的变量。小米包常见 set IMAGE_DIR=%CURRENT_SCRIPT_DIR%images 这种中转，
        // 只认内置变量名会把整包判成「镜像不存在」，所以这里老实做一遍赋值表。
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in ReadLines(scriptPath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0) continue;

            var assignment = MatchAssignment(line, isShell);
            if (assignment is { } kv)
            {
                vars[kv.Key] = Absolutize(ExpandVars(kv.Value.Trim('"'), romRoot, vars), romRoot);
                continue;
            }

            if (IsPreamble(line)) continue;

            var conditional = false;
            var work        = line;

            // if [not] exist <路径> fastboot ...
            var mIf = Regex.Match(work, @"^if\s+(not\s+)?exist\s+(\S+)\s+(.+)$", RegexOptions.IgnoreCase);
            if (mIf.Success)
            {
                var negate = mIf.Groups[1].Success;
                var probe  = Absolutize(ExpandVars(mIf.Groups[2].Value.Trim('"'), romRoot, vars), romRoot);
                var exists = SafeFileExists(probe);

                // 条件不成立就整行跳过，这是官方脚本表达「这个机型没有该分区」的方式
                if (exists == negate) continue;

                conditional = true;
                work        = mIf.Groups[3].Value;
            }

            var tokens = Tokenize(work);
            var fbAt   = tokens.FindIndex(t => t.Contains("fastboot", StringComparison.OrdinalIgnoreCase));
            if (fbAt < 0) continue;

            var args = tokens.Skip(fbAt + 1)
                             .Where(t => t.Length > 0 && !IsPlaceholder(t))
                             .Select(t => ExpandVars(t, romRoot, vars))
                             .ToList();
            if (args.Count == 0) continue;

            var verb = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToList();

            var step = BuildStep(steps.Count + 1, verb, rest, romRoot, conditional, warnings);
            if (step is null) continue;

            steps.Add(step);
        }

        return new FlashPlan
        {
            RomRoot      = romRoot,
            ScriptPath   = Path.GetFullPath(scriptPath),
            ScriptName   = name,
            ModeLabel    = DescribeMode(baseName),
            KeepsUserData = baseName.ToLowerInvariant().Contains("except_"),
            Relocks      = baseName.ToLowerInvariant().Contains("lock"),
            Steps        = steps,
            Warnings     = warnings,
        };
    }

    /// <summary>把一条命令翻译成步骤；返回 null 表示这条不用执行（例如 getvar 这类只读查询）。</summary>
    private static FlashStep? BuildStep(int index, string verb, List<string> rest, string romRoot,
                                        bool conditional, List<string> warnings)
    {
        switch (verb)
        {
            case "flash":
            {
                if (rest.Count < 2)
                {
                    warnings.Add($"flash 命令参数不完整，已跳过：flash {string.Join(" ", rest)}");
                    return null;
                }

                var partition = rest[0];
                var image     = Absolutize(rest[1], romRoot);

                if (!SafeFileExists(image))
                {
                    warnings.Add($"镜像不存在，已跳过：{partition} → {rest[1]}");
                    return null;
                }

                // 后面可能还有 --slot 之类的附加开关，一并带上
                var args = string.Join(" ", new[] { "flash", partition, Quote(image) }.Concat(rest.Skip(2)));

                return new FlashStep
                {
                    Index         = index,
                    Kind          = StepKind.Flash,
                    Verb          = verb,
                    Partition     = partition,
                    ImagePath     = image,
                    Arguments     = args,
                    Note          = $"刷入 {partition}",
                    IsConditional = conditional,
                    ImageSize     = SafeLength(image),
                };
            }

            case "erase":
            case "format":
            {
                if (rest.Count == 0)
                {
                    warnings.Add($"{verb} 命令缺少分区名，已跳过。");
                    return null;
                }

                var partition = rest[0];
                return new FlashStep
                {
                    Index         = index,
                    Kind          = verb == "erase" ? StepKind.Erase : StepKind.Format,
                    Verb          = verb,
                    Partition     = partition,
                    Arguments     = string.Join(" ", rest),
                    Note          = $"{(verb == "erase" ? "擦除" : "格式化")} {partition}",
                    IsConditional = conditional,
                };
            }

            case "-w":
                return new FlashStep
                {
                    Index     = index,
                    Kind      = StepKind.Wipe,
                    Verb      = verb,
                    Arguments = "-w",
                    Note      = "清空 userdata 与 metadata",
                };

            case "reboot":
            case "reboot-bootloader":
            case "reboot-fastboot":
            case "reboot-recovery":
            case "reboot-edl":
                return new FlashStep
                {
                    Index     = index,
                    Kind      = StepKind.Reboot,
                    Verb      = verb,
                    Arguments = string.Join(" ", rest.Prepend(verb)),
                    Note      = verb == "reboot" ? "重启进系统" : $"重启到 {verb["reboot-".Length..]}",
                };

            // getvar 是只读查询：官方脚本用它做机型判断，我们不需要执行
            case "getvar":
            case "getvar-all":
            case "devices":
            case "help":
                return null;

            default:
                return new FlashStep
                {
                    Index         = index,
                    Kind          = StepKind.Raw,
                    Verb          = verb,
                    Partition     = rest.Count > 0 ? rest[0] : string.Empty,
                    Arguments     = string.Join(" ", new[] { verb }.Concat(rest)),
                    Note          = DescribeRaw(verb),
                    IsConditional = conditional,
                };
        }
    }

    private static string DescribeRaw(string verb) => verb switch
    {
        "flashing"                 => "bootloader 加锁 / 解锁",
        "oem"                      => "厂商 OEM 指令",
        "set_active"               => "切换活动槽位",
        "resize-logical-partition" => "调整逻辑分区大小",
        "delete-logical-partition" => "删除逻辑分区",
        "create-logical-partition" => "创建逻辑分区",
        "update"                   => "sideload 刷入 OTA 包",
        "continue"                 => "继续启动",
        _                          => verb,
    };

    // ==================== 执行 ====================

    /// <summary>
    /// 逐条执行刷写计划。<paramref name="stopOnError"/> 为 true 时第一条失败就中止。
    /// 每步开始前检查取消令牌，取消后已下发的命令会跑完，不会再发新命令。
    /// </summary>
    public static async Task<FlashResult> ExecuteAsync(
        FlashPlan plan,
        Action<string> log,
        IProgress<FlashProgress>? progress = null,
        bool stopOnError = true,
        CancellationToken ct = default)
    {
        if (!FastbootService.IsAvailable)
            return new FlashResult(false, 0, 0, 0, new[] { "未找到 fastboot 工具。" }, false);

        var failures = new List<string>();
        int done = 0, failed = 0, skipped = 0;
        var aborted = false;

        var total = plan.Steps.Count;

        for (var i = 0; i < total; i++)
        {
            var step = plan.Steps[i];

            if (ct.IsCancellationRequested)
            {
                aborted = true;
                log($"已取消，剩余 {total - i} 步未执行。");
                break;
            }

            progress?.Report(new FlashProgress(i + 1, total, step.Note));

            // 解析时已校验过一次，这里再确认防用户中途删了文件
            if (step.Kind == StepKind.Flash && step.ImagePath is { } image && !SafeFileExists(image))
            {
                skipped++;
                log($"[{i + 1}/{total}] 跳过（镜像不存在）：{Path.GetFileName(image)}");
                continue;
            }

            // 回锁这类会清数据的步骤单独标出来，日志里好认
            var risky = step.Verb is "flashing" or "oem" or "-w";
            log($"[{i + 1}/{total}] {step.Note}{(risky ? "  ← 会清空数据" : string.Empty)}");
            log($"    > fastboot {step.Arguments}");

            if (step.Kind == StepKind.Raw && step.Verb is "flashing" or "oem")
                log("    提示：该指令通常需要手机屏幕确认，请留意设备。");

            var stepFailed = false;
            try
            {
                var ok = await FastbootService.RunRawAsync(step.Arguments, line =>
                {
                    if (LooksFailed(line)) stepFailed = true;
                    log("    " + line);
                });

                if (!ok) stepFailed = true;
            }
            catch (Exception ex)
            {
                stepFailed = true;
                log($"    [错误] {ex.Message}");
            }

            if (stepFailed)
            {
                failed++;
                failures.Add($"{step.Note}（fastboot {step.Arguments}）");
                log($"    失败：{step.Note}");

                if (stopOnError)
                {
                    aborted = true;
                    log($"已在第 {i + 1} 步停止。请查看上方报错，处理后可重新执行。");
                    break;
                }
            }
            else
            {
                done++;
            }
        }

        return new FlashResult(failed == 0 && !aborted, done, failed, skipped, failures, aborted);
    }

    /// <summary>单行输出是否表示失败。fastboot 出错时退出码不一定是非 0，所以还要看文本。</summary>
    private static bool LooksFailed(string output)
    {
        var t = output.ToLowerInvariant();

        return t.Contains("failed") || t.Contains("error") || t.Contains("cannot")
            || t.Contains("not allowed") || t.Contains("not supported")
            || t.Contains("unknown partition") || t.Contains("doesn't exist")
            || t.Contains("does not exist") || t.Contains("invalid")
            || t.Contains("no such") || t.Contains("couldn't");
    }

    // ==================== 文本处理 ====================

    /// <summary>
    /// 读脚本。优先按严格 UTF-8 解，失败退回 Latin1（永不抛）。
    /// 我们只关心 ASCII 命令，中文注释即便是乱码也不影响解析。
    /// </summary>
    private static IEnumerable<string> ReadLines(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(false, throwOnInvalidBytes: true));
        }
        catch
        {
            try { text = File.ReadAllText(path, Encoding.Latin1); }
            catch { return Array.Empty<string>(); }
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>去掉行尾注释。bat 用 :: 或 rem，sh 用 #。</summary>
    private static string StripComment(string line)
    {
        var t = line.Trim();

        if (t.StartsWith("::", StringComparison.Ordinal)) return string.Empty;
        if (t.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (t.StartsWith("#", StringComparison.Ordinal)) return string.Empty;

        // 行内注释只在 sh 里安全处理；bat 的 & 是命令分隔符，不能乱切
        var hash = t.IndexOf(" #", StringComparison.Ordinal);
        if (hash > 0) t = t[..hash];

        return t;
    }

    /// <summary>跳过脚本里与刷写无关的样板行。</summary>
    private static bool IsPreamble(string line)
    {
        var t = line.TrimStart('@', ' ');

        return t.StartsWith("echo", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("set ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("setlocal", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("path=", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("cd ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("chcp", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("pause", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("exit", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("goto", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith(":")              // 标签
            || t.StartsWith("#!");            // shebang
    }

    /// <summary>按空白切词，双引号内的空白不切。</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb     = new StringBuilder();
        var quoted = false;

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>%* / $* / %1 这类脚本参数占位符，不参与实际命令。</summary>
    private static bool IsPlaceholder(string token)
    {
        var t = token.Trim('"');

        return t is "%*" or "$*" or "%%" or "$@"
            || Regex.IsMatch(t, @"^%[0-9*]$")
            || Regex.IsMatch(t, @"^\$[0-9*]$");
    }

    /// <summary>识别脚本里的变量赋值。bat 用 <c>set NAME=...</c>，shell 用 <c>NAME=...</c>。</summary>
    private static (string Key, string Value)? MatchAssignment(string line, bool isShell)
    {
        if (isShell)
        {
            var m = Regex.Match(line, @"^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=(.*)$");
            return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
        }

        // set NAME=value 与 set "NAME=value" 两种写法
        var b = Regex.Match(line, @"^set\s+(?:""([^""=]+)=([^""]*)""|([^=\s]+)=(.*))$", RegexOptions.IgnoreCase);
        if (!b.Success) return null;

        return b.Groups[1].Success
            ? (b.Groups[1].Value, b.Groups[2].Value)
            : (b.Groups[3].Value, b.Groups[4].Value);
    }

    /// <summary>
    /// 展开脚本里的变量。替换值一律走 MatchEvaluator，避免路径里出现 $ 时被当成替换组。
    /// 未知变量原样保留 —— 后面的文件存在性检查会把它变成一条明确的警告，而不是静默出错。
    /// </summary>
    private static string ExpandVars(string token, string romRoot, IDictionary<string, string> vars)
    {
        if (token.Length == 0) return token;

        var dir = romRoot + Path.DirectorySeparatorChar;
        var t   = token;

        t = Regex.Replace(t, @"%~dp0", _ => dir, RegexOptions.IgnoreCase);

        // Windows 风格 %NAME%
        t = Regex.Replace(t, @"%([A-Za-z_][A-Za-z0-9_]*)%", m =>
        {
            var key = m.Groups[1].Value;
            if (vars.TryGetValue(key, out var value)) return value;

            return key.ToUpperInvariant() switch
            {
                "CURRENT_SCRIPT_DIR" or "SCRIPT_DIR" or "CD" or "ROM_DIR" => dir,
                _ => m.Value,
            };
        });

        // shell 风格 $NAME / ${NAME}（$* / $1 之类不含字母，不会被匹配到）
        t = Regex.Replace(t, @"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?", m =>
        {
            var key = m.Groups[1].Value;
            if (vars.TryGetValue(key, out var value)) return value;

            return key.ToUpperInvariant() switch
            {
                "CURRENT_SCRIPT_DIR" or "SCRIPT_DIR" or "PWD" => dir,
                _ => m.Value,
            };
        });

        return t;
    }

    /// <summary>把相对路径补成绝对路径；不像路径的（分区名之类）原样返回。</summary>
    private static string Absolutize(string token, string romRoot)
    {
        if (token.Length == 0) return token;
        if (!LooksLikePath(token)) return token;

        try
        {
            if (Path.IsPathRooted(token)) return Path.GetFullPath(token);
            return Path.GetFullPath(Path.Combine(romRoot, token));
        }
        catch
        {
            return token;
        }
    }

    private static bool LooksLikePath(string token)
    {
        if (token.Contains('\\') || token.Contains('/')) return true;

        var ext = Path.GetExtension(token).ToLowerInvariant();
        return ext is ".img" or ".bin" or ".xml" or ".mbn" or ".elf" or ".melf" or ".tar" or ".zip";
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
