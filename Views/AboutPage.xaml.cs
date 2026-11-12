using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mosytool.Models.Api;
using Mosytool.Services;
using Mosytool.Services.Api;

namespace Mosytool.Views;

public partial class AboutPage : Page
{
    /// <summary>拉取头像用；超时短一点，失败了就退回占位，不拖累界面。</summary>
    private static readonly HttpClient AvatarHttp = CreateAvatarClient();

    public AboutPage()
    {
        InitializeComponent();
        AboutVersion.Text = $"版本 {AppInfo.DisplayVersion}";

        // 主题单选的初始状态按配置回填（不在 XAML 里写 IsChecked，避免和配置打架）
        SelectThemeRadio(ThemeService.Mode);

        ApiBaseUrlBox.PlaceholderText = ApiClient.DefaultBaseUrl;
        ApiBaseUrlBox.Text = SettingsService.ApiBaseUrl ?? string.Empty;
        ApiChannelBox.SelectedIndex = string.Equals(SettingsService.ApiChannel, "beta",
            StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        _ = LoadAvatarAsync(AvatarShiyi, "shiyi", "228326485");
        _ = LoadAvatarAsync(AvatarMywl, "mywl", "2712051569");

        // 技术 / 测试支持名单：优先由接口填充，拉取失败时保留下面这份内置兜底
        _ = InitSupportAsync();
    }

    /// <summary>接口可用就渲染接口名单；不可用才保留内置兜底名单并给它加载头像。</summary>
    private async Task InitSupportAsync()
    {
        if (await LoadContributorsAsync().ConfigureAwait(true)) return;

        await Task.WhenAll(
            LoadAvatarAsync(AvatarSupMywl, "mywl", "2712051569"),
            LoadAvatarAsync(AvatarSupWanmo, "wanmo", "2062383378"),
            LoadAvatarAsync(AvatarSupXxx, "xxxxxxx", "2963932228"),
            LoadAvatarAsync(AvatarSupPabibu, "pabibu", "869675380"));
    }

    /// <summary>
    /// 从接口拉取「技术 / 测试支持」名单填充 <see cref="SupportPanel"/>。
    /// 拿不到数据（网络失败 / 后端未配置）时直接返回，界面保留 XAML 里的内置兜底名单。
    /// </summary>
    private async Task<bool> LoadContributorsAsync()
    {
        List<Contributor> list;
        try
        {
            list = await ContributorService.ListAsync().ConfigureAwait(true);
        }
        catch
        {
            return false;
        }

        var items = list
            .Select(c => new { Contributor = c, Name = ApiClient.Normalize(c.Name) })
            .Where(x => x.Name is not null)
            .ToList();

        if (items.Count == 0) return false;

        SupportPanel.Children.Clear();
        foreach (var item in items)
            SupportPanel.Children.Add(BuildContributorItem(item.Contributor, item.Name!));

        return true;
    }

    /// <summary>
    /// 把一位致谢成员渲染成「头像 + 昵称 + 角色 + 介绍」的小块。
    /// 之前只显示昵称、把角色塞进 ToolTip，介绍完全看不到，这里改成全部铺开显示。
    /// </summary>
    private static StackPanel BuildContributorItem(Contributor contributor, string name)
    {
        var avatar = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = FindBrush("Win12AccentGradientBrush"),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var initial = new TextBlock
        {
            Text = name[..1].ToUpperInvariant(),
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = FindBrush("TextOnAccentFillColorPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        avatar.Child = initial;

        var info = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        info.Children.Add(new TextBlock
        {
            Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = FindBrush("TextFillColorPrimaryBrush"),
        });

        var role = ApiClient.Normalize(contributor.Role);
        if (role is not null)
        {
            info.Children.Add(new TextBlock
            {
                Text = role, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = FindBrush("AccentTextFillColorPrimaryBrush"),
            });
        }

        var description = ApiClient.Normalize(contributor.Description);
        if (description is not null)
        {
            info.Children.Add(new TextBlock
            {
                Text = description, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap, LineHeight = 17,
                Foreground = FindBrush("TextFillColorSecondaryBrush"),
            });
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Width       = 260,
            Margin      = new Thickness(0, 0, 18, 14),
        };
        row.Children.Add(avatar);
        row.Children.Add(info);

        _ = LoadContributorAvatarAsync(avatar, initial, contributor, name);
        return row;
    }

    /// <summary>接口头像 → QQ 头像 → tools\avatars\{name} 本地图片，依次尝试，最后保留首字底色。</summary>
    private static async Task LoadContributorAvatarAsync(
        Border host, TextBlock initial, Contributor contributor, string name)
    {
        var avatarUrl = ApiClient.Normalize(contributor.AvatarUrl);
        if (avatarUrl is not null && await TryApplyRemoteAsync(host, new[] { avatarUrl }).ConfigureAwait(true))
        {
            initial.Visibility = Visibility.Collapsed;
            return;
        }

        var qq = ApiClient.Normalize(contributor.Qq);
        if (qq is not null && await TryApplyRemoteAsync(host, QqAvatarUrls(qq)).ConfigureAwait(true))
        {
            initial.Visibility = Visibility.Collapsed;
            return;
        }

        if (TryApplyLocal(host, name)) initial.Visibility = Visibility.Collapsed;
    }

    private static Brush? FindBrush(string key) => Application.Current?.TryFindResource(key) as Brush;

    private static HttpClient CreateAvatarClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MosyTool/1.0");
        return client;
    }

    /// <summary>
    /// 作者头像：优先按 QQ 号从 qlogo 接口拉取，取不到再找 tools\avatars\ 下的
    /// {name}.png / .jpg / .jpeg / .webp（离线或想换图时用），全都拿不到就保留默认的渐变底色 + 首字。
    /// tools\ 既可能是 exe 同级的真实目录，也可能是单文件发布时首启解压出的数据目录。
    /// </summary>
    private static async Task LoadAvatarAsync(Border host, string name, string qq)
    {
        if (await TryApplyRemoteAsync(host, QqAvatarUrls(qq)).ConfigureAwait(true)) return;
        TryApplyLocal(host, name);
    }

    /// <summary>同一个头像有多个入口域名，q1 不通时换 q4 或 headimg_dl，避免单点被墙/超时。</summary>
    private static string[] QqAvatarUrls(string qq) => new[]
    {
        $"https://q1.qlogo.cn/g?b=qq&nk={qq}&s=640",
        $"https://q4.qlogo.cn/g?b=qq&nk={qq}&s=640",
        $"https://q.qlogo.cn/headimg_dl?dst_uin={qq}&spec=640&img_type=jpg",
    };

    /// <summary>按顺序尝试远端地址，任一成功即返回 true。</summary>
    private static async Task<bool> TryApplyRemoteAsync(Border host, IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            try
            {
                var bytes = await AvatarHttp.GetByteArrayAsync(url).ConfigureAwait(true);
                if (bytes.Length > 0 && ApplyAvatar(host, bytes)) return true;
            }
            catch
            {
                // 网络不通、超时、DNS 失败……换下一个入口
            }
        }

        return false;
    }

    /// <summary>回退到 tools\avatars\{name}.png|jpg|jpeg|webp，离线或想换图时用。</summary>
    private static bool TryApplyLocal(Border host, string name)
    {
        foreach (var dir in ToolPaths.Dirs)
        {
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            {
                var file = Path.Combine(dir, "avatars", name + ext);
                if (!File.Exists(file)) continue;

                try
                {
                    if (ApplyAvatar(host, File.ReadAllBytes(file))) return true;
                }
                catch
                {
                    // 图片损坏就退回默认的首字底色
                }
            }
        }

        return false;
    }

    /// <summary>把图片字节铺到圆形头像框里；解码失败返回 false。</summary>
    private static bool ApplyAvatar(Border host, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption   = BitmapCacheOption.OnLoad;
            bmp.StreamSource  = stream;
            bmp.EndInit();
            bmp.Freeze();

            host.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 主题色 ----------

    private void AccentColor_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string hex })
        {
            var color = ColorConverter.ConvertFromString(hex);
            if (color is Color c) ApplyAccent(c);
        }
    }

    private void CustomAccent_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color    = System.Drawing.Color.FromArgb(SettingsService.AccentColor.A, SettingsService.AccentColor.R,
                                                     SettingsService.AccentColor.G, SettingsService.AccentColor.B),
            FullOpen = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var c = dialog.Color;
        ApplyAccent(Color.FromArgb(c.A, c.R, c.G, c.B));
    }

    private void ResetAccent_Click(object sender, RoutedEventArgs e)
    {
        ApplyAccent(Color.FromRgb(0x3B, 0x91, 0xD8));
    }

    private static void ApplyAccent(Color color)
    {
        SettingsService.SaveAccentColor(color);
        App.ApplyAccent(color);
    }

    // ---------- 外观（深 / 浅色） ----------

    /// <summary>回填单选状态时抑制 Checked 事件，免得构造期就把配置重写一遍。</summary>
    private bool _suppressThemeEvent;

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeEvent) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;

        ThemeService.SetMode(ThemeService.ParseMode(tag));
    }

    private void SelectThemeRadio(AppTheme mode)
    {
        _suppressThemeEvent = true;
        try
        {
            switch (mode)
            {
                case AppTheme.Light:
                    ThemeLightRadio.IsChecked = true;
                    break;
                case AppTheme.Dark:
                    ThemeDarkRadio.IsChecked = true;
                    break;
                default:
                    ThemeSystemRadio.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _suppressThemeEvent = false;
        }
    }

    // ---------- 服务器设置（默认隐藏的维护入口） ----------

    /// <summary>解锁「服务器设置」需要连点的次数与间隔上限。</summary>
    private const int UnlockClickCount = 5;

    private static readonly TimeSpan UnlockClickTimeout = TimeSpan.FromSeconds(2);

    private DateTime _unlockClickStart = DateTime.MinValue;
    private int      _unlockClickCount;

    /// <summary>
    /// 版本号连点 5 下展开 / 收起「服务器设置」卡片。
    /// 卡片默认 Collapsed（普通用户看不到），解锁只在本次运行内有效，重启自动恢复隐藏。
    /// 相邻两次点击间隔超过 <see cref="UnlockClickTimeout"/> 就重新计数，
    /// 免得用户零零散散点几下就把它点出来。
    /// </summary>
    private void AboutVersion_Click(object sender, MouseButtonEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _unlockClickStart > UnlockClickTimeout) _unlockClickCount = 0;

        _unlockClickStart = now;
        if (++_unlockClickCount < UnlockClickCount) return;

        _unlockClickCount = 0;
        ServerSettingsCard.Visibility = ServerSettingsCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SaveApi_Click(object sender, RoutedEventArgs e)
    {
        var url = ApiBaseUrlBox.Text.Trim();
        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ApiStatusText.Text = "接口地址格式不正确";
            return;
        }

        var channel = (ApiChannelBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "stable";
        SettingsService.SaveApiOptions(url, channel);
        ApiStatusText.Text = "已保存，接口地址已生效";
    }

    private void ResetApi_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.SaveApiOptions(null, "stable");
        ApiBaseUrlBox.Text = string.Empty;
        ApiChannelBox.SelectedIndex = 0;
        ApiStatusText.Text = $"已恢复默认（{ApiClient.DefaultBaseUrl}）";
    }

    /// <summary>用当前生效的接口地址实拉一次公告，确认后台可达且返回结构正常。</summary>
    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        TestApiButton.IsEnabled = false;
        ApiStatusText.Text = $"正在连接 {ApiClient.BaseUrl} …";

        try
        {
            var data = await AnnouncementService.ListAsync(announceType: null, keyword: null,
                                                          limit: 5, ignoreCache: true).ConfigureAwait(true);
            ApiStatusText.Text = $"连接正常（{ApiClient.BaseUrl}），当前公告 {data.Count} 条";
        }
        catch (ApiException ex)
        {
            ApiStatusText.Text = ex.IsNetworkError
                ? $"连不上 {ApiClient.BaseUrl}，请检查网络或接口地址"
                : $"后台返回错误：{ex.Message}";
        }
        catch (Exception ex)
        {
            ApiStatusText.Text = $"连接失败：{ex.Message}";
        }
        finally
        {
            TestApiButton.IsEnabled = true;
        }
    }

    /// <summary>用浏览器打开 MosyTool 后台管理系统。</summary>
    private void OpenAdmin_Click(object sender, RoutedEventArgs e) => OpenUrl(ApiClient.BaseUrl);

    // ---------- 链接 ----------

    private void AboutUrl_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => OpenUrl(AboutUrl.Text);

    private void PenumbraRepo_Click(object sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/shomykohai/penumbra");

    /// <summary>致谢区块里各按钮统一走这里，URL 放在 Tag 上。</summary>
    private void RepoLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* 忽略 */ }
    }
}
