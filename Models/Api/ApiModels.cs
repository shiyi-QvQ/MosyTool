using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Mosytool.Models.Api;

// ===== 统一信封 =====
// 后端约定：code = 0 成功且才存在 data；失败时 data 直接省略，message 可直接展示。
// 注意 time 字段一律 RFC3339，且「可能为 null」，所以全部用 string? 接，不做自动解析。

/// <summary>通用响应信封。</summary>
public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("code")]    public int    Code    { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("data")]    public T?      Data    { get; set; }
}

/// <summary>标准分页结构（with page / page_size）。</summary>
public sealed class PagedList<T>
{
    [JsonPropertyName("list")]      public List<T> List     { get; set; } = new();
    [JsonPropertyName("total")]     public int     Total    { get; set; }
    [JsonPropertyName("page")]      public int     Page     { get; set; }
    [JsonPropertyName("page_size")] public int     PageSize { get; set; }
}

/// <summary>聚合结构（公告 / 品牌 / 机型 / 致谢 / 上游）：只有 list + total + server_time。</summary>
public sealed class AggList<T>
{
    [JsonPropertyName("list")]        public List<T> List       { get; set; } = new();
    [JsonPropertyName("total")]       public int     Total      { get; set; }
    [JsonPropertyName("brand")]       public string? Brand      { get; set; }
    [JsonPropertyName("server_time")] public string? ServerTime { get; set; }
}

// ===== 二、远程公告 =====

public sealed class Announcement
{
    [JsonPropertyName("id")]            public long    Id           { get; set; }
    [JsonPropertyName("title")]         public string? Title        { get; set; }
    [JsonPropertyName("content")]       public string? Content      { get; set; }

    /// <summary>notice 普通 / important 重要 / urgent 紧急，决定配色与图标。</summary>
    [JsonPropertyName("level")]         public string? Level        { get; set; }

    /// <summary>notice 普通公告 / popup 弹窗公告 / home 首页公告，决定展示位置。</summary>
    [JsonPropertyName("announce_type")] public string? AnnounceType { get; set; }

    [JsonPropertyName("status")]        public string? Status       { get; set; }
    [JsonPropertyName("pinned")]        public bool    Pinned       { get; set; }
    [JsonPropertyName("author")]        public string? Author       { get; set; }
    [JsonPropertyName("publish_at")]    public string? PublishAt    { get; set; }
    [JsonPropertyName("expire_at")]     public string? ExpireAt     { get; set; }
    [JsonPropertyName("created_at")]    public string? CreatedAt    { get; set; }
    [JsonPropertyName("updated_at")]    public string? UpdatedAt    { get; set; }
}

// ===== 三、在线更新 =====

public sealed class UpdateCheck
{
    [JsonPropertyName("has_update")]  public bool           HasUpdate  { get; set; }

    /// <summary>已经是 force &amp;&amp; has_update 的结果。⚠️ 无匹配版本时后端不返回该字段，故为可空。</summary>
    [JsonPropertyName("force")]       public bool?          Force      { get; set; }

    [JsonPropertyName("current")]     public string?        Current    { get; set; }
    [JsonPropertyName("platform")]    public string?        Platform   { get; set; }
    [JsonPropertyName("channel")]     public string?        Channel    { get; set; }

    /// <summary>无匹配版本时为 null，必须先判空。</summary>
    [JsonPropertyName("latest")]      public AppRelease?    Latest     { get; set; }

    [JsonPropertyName("server_time")] public string?        ServerTime { get; set; }
}

/// <summary>一个已发布的客户端版本（update/latest.latest 与 updates 列表元素同构）。</summary>
public sealed class AppRelease
{
    [JsonPropertyName("id")]           public long          Id          { get; set; }
    [JsonPropertyName("platform")]     public string?       Platform    { get; set; }
    [JsonPropertyName("channel")]      public string?       Channel     { get; set; }
    [JsonPropertyName("version_name")] public string?       VersionName { get; set; }
    [JsonPropertyName("version_code")] public long          VersionCode { get; set; }
    [JsonPropertyName("download_url")] public string?       DownloadUrl { get; set; }
    [JsonPropertyName("mirror_urls")]  public List<string>  MirrorUrls  { get; set; } = new();
    [JsonPropertyName("file_size")]    public long          FileSize    { get; set; }
    [JsonPropertyName("sha256")]       public string?       Sha256      { get; set; }
    [JsonPropertyName("changelog")]    public string?       Changelog   { get; set; }
    [JsonPropertyName("force_update")] public bool          ForceUpdate { get; set; }
    [JsonPropertyName("min_version")]  public string?       MinVersion  { get; set; }
    [JsonPropertyName("status")]       public string?       Status      { get; set; }
    [JsonPropertyName("published_at")] public string?       PublishedAt { get; set; }
    [JsonPropertyName("created_at")]   public string?       CreatedAt   { get; set; }
    [JsonPropertyName("updated_at")]   public string?       UpdatedAt   { get; set; }
}

// ===== 四、官方 ROM 包 =====

/// <summary>
/// 一条 ROM 记录。字段几乎都可能为空（后端是同步上游得来的），客户端不要假设 published_at /
/// ota_version / android_version / changelog_url 一定有值。
/// </summary>
public sealed class RomPackage
{
    [JsonPropertyName("id")]                 public long    Id              { get; set; }
    [JsonPropertyName("brand")]              public string? Brand           { get; set; }
    [JsonPropertyName("device_name")]        public string? DeviceName      { get; set; }
    [JsonPropertyName("model_code")]         public string? ModelCode       { get; set; }
    [JsonPropertyName("region")]             public string? Region          { get; set; }
    [JsonPropertyName("version_name")]       public string? VersionName     { get; set; }
    [JsonPropertyName("ota_version")]        public string? OtaVersion      { get; set; }
    [JsonPropertyName("android_version")]    public string? AndroidVersion  { get; set; }
    [JsonPropertyName("coloros_version")]    public string? ColorOsVersion  { get; set; }
    [JsonPropertyName("security_patch")]     public string? SecurityPatch   { get; set; }

    /// <summary>full 完整包 / ota 增量包。</summary>
    [JsonPropertyName("rom_type")]           public string? RomType         { get; set; }

    [JsonPropertyName("file_name")]          public string? FileName        { get; set; }
    [JsonPropertyName("file_size")]          public long    FileSize        { get; set; }

    /// <summary>官方下载页地址，通常需要跳转。</summary>
    [JsonPropertyName("download_url")]       public string? DownloadUrl     { get; set; }

    /// <summary>可直接下载的直链，可能为空或已过期（带 Expires）。</summary>
    [JsonPropertyName("direct_url")]         public string? DirectUrl       { get; set; }

    [JsonPropertyName("changelog_url")]      public string? ChangelogUrl    { get; set; }

    /// <summary>为 true 时下载必须带 <c>userid: oplus-ota|</c> 请求头，否则拿不到内容。</summary>
    [JsonPropertyName("need_userid_header")] public bool    NeedUseridHeader { get; set; }

    [JsonPropertyName("source")]             public string? Source          { get; set; }
    [JsonPropertyName("source_path")]        public string? SourcePath      { get; set; }
    [JsonPropertyName("published_at")]       public string? PublishedAt     { get; set; }
    [JsonPropertyName("status")]             public string? Status          { get; set; }
    [JsonPropertyName("remark")]             public string? Remark          { get; set; }
    [JsonPropertyName("synced_at")]          public string? SyncedAt        { get; set; }
    [JsonPropertyName("created_at")]         public string? CreatedAt       { get; set; }
    [JsonPropertyName("updated_at")]         public string? UpdatedAt       { get; set; }
}

/// <summary>/api/public/roms/brands 的一项。brand 可能为空串（展示为「未知品牌」，但不可用它筛选）。</summary>
public sealed class RomBrand
{
    [JsonPropertyName("brand")]        public string? Brand       { get; set; }
    [JsonPropertyName("count")]        public int     Count       { get; set; }
    [JsonPropertyName("device_count")] public int     DeviceCount { get; set; }
}

/// <summary>机型聚合项。同一机型的不同地区是不同的行，客户端需按 device_name 归并。</summary>
public sealed class RomDevice
{
    [JsonPropertyName("device_name")] public string? DeviceName { get; set; }
    [JsonPropertyName("brand")]       public string? Brand      { get; set; }
    [JsonPropertyName("region")]      public string? Region     { get; set; }
    [JsonPropertyName("count")]       public int     Count      { get; set; }
    [JsonPropertyName("latest_at")]   public string? LatestAt   { get; set; }
}

/// <summary>/api/public/rom/source：上游仓库与可用代理。</summary>
public sealed class RomSource
{
    [JsonPropertyName("repo")]         public string?     Repo        { get; set; }
    [JsonPropertyName("branch")]       public string?     Branch      { get; set; }
    [JsonPropertyName("repo_url")]     public string?     RepoUrl     { get; set; }
    [JsonPropertyName("archive_url")]  public string?     ArchiveUrl  { get; set; }
    [JsonPropertyName("prefer")]       public string?     Prefer      { get; set; }
    [JsonPropertyName("source")]       public string?     Source      { get; set; }
    [JsonPropertyName("server_api")]   public string?     ServerApi   { get; set; }
    [JsonPropertyName("raw_sources")]  public List<RomEndpoint> RawSources  { get; set; } = new();
    [JsonPropertyName("api_sources")]  public List<RomEndpoint> ApiSources  { get; set; } = new();
    [JsonPropertyName("server_time")]  public string?     ServerTime  { get; set; }
}

/// <summary>上游地址项。kind：direct 直连 / cdn CDN / proxy 自建代理。</summary>
public sealed class RomEndpoint
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("base")] public string? Base { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

// ===== 五、致谢人员 =====

public sealed class Contributor
{
    [JsonPropertyName("id")]           public long    Id          { get; set; }
    [JsonPropertyName("name")]         public string? Name        { get; set; }
    [JsonPropertyName("role")]         public string? Role        { get; set; }
    [JsonPropertyName("description")]  public string? Description { get; set; }
    [JsonPropertyName("avatar_url")]   public string? AvatarUrl   { get; set; }
    [JsonPropertyName("homepage_url")] public string? HomepageUrl { get; set; }
    [JsonPropertyName("qq")]           public string? Qq          { get; set; }
    [JsonPropertyName("sort_order")]   public int     SortOrder   { get; set; }
}

// ===== 意见反馈（工单）=====

/// <summary>
/// 提交反馈的请求体。后端开启了「未知字段拒收」，且 ip / user_agent 由服务端采集，
/// 客户端绝不能传这两个字段。所有文本字段都是 string 类型。
/// </summary>
public sealed class FeedbackSubmitRequest
{
    [JsonPropertyName("content")]      public string  Content     { get; set; } = string.Empty;
    [JsonPropertyName("category")]     public string? Category    { get; set; }
    [JsonPropertyName("email")]        public string? Email       { get; set; }
    [JsonPropertyName("contact")]      public string? Contact     { get; set; }
    [JsonPropertyName("contact_type")] public string? ContactType { get; set; }
    [JsonPropertyName("app_version")]  public string? AppVersion  { get; set; }
    [JsonPropertyName("platform")]     public string? Platform    { get; set; }
    [JsonPropertyName("device_info")]  public string? DeviceInfo  { get; set; }
}

public sealed class FeedbackSubmitResult
{
    /// <summary>工单编号，客户端必须本地保存，后续查询与补充都要用。</summary>
    [JsonPropertyName("id")]            public long    Id           { get; set; }

    /// <summary>false 表示没发出受理邮件（未填邮箱或后台没配 SMTP），用户拿不到查询令牌。</summary>
    [JsonPropertyName("mail_notified")] public bool    MailNotified { get; set; }

    [JsonPropertyName("message")]       public string? Message      { get; set; }
}

public sealed class FeedbackDetail
{
    [JsonPropertyName("id")]          public long                Id         { get; set; }
    [JsonPropertyName("category")]    public string?             Category   { get; set; }
    [JsonPropertyName("content")]     public string?             Content    { get; set; }

    /// <summary>pending / processing / resolved / ignored。</summary>
    [JsonPropertyName("status")]      public string?             Status     { get; set; }
    [JsonPropertyName("status_text")] public string?             StatusText { get; set; }

    /// <summary>管理员最新回复（兼容旧字段），建议改用 replies。</summary>
    [JsonPropertyName("reply")]       public string?             Reply      { get; set; }

    /// <summary>往来消息，按时间升序。</summary>
    [JsonPropertyName("replies")]     public List<FeedbackReply>  Replies    { get; set; } = new();

    [JsonPropertyName("created_at")]  public string?             CreatedAt  { get; set; }
    [JsonPropertyName("updated_at")]  public string?             UpdatedAt  { get; set; }

    /// <summary>status 为 resolved / ignored 时为 true。</summary>
    [JsonPropertyName("closed")]      public bool                Closed     { get; set; }

    /// <summary>等价于 !closed。</summary>
    [JsonPropertyName("can_reply")]   public bool                CanReply   { get; set; }

    [JsonPropertyName("closed_text")] public string?             ClosedText { get; set; }
}

public sealed class FeedbackReply
{
    [JsonPropertyName("id")]          public long    Id         { get; set; }
    [JsonPropertyName("feedback_id")] public long    FeedbackId { get; set; }

    /// <summary>user 反馈方 / admin 管理员。</summary>
    [JsonPropertyName("author")]      public string? Author     { get; set; }

    [JsonPropertyName("content")]     public string? Content    { get; set; }
    [JsonPropertyName("mail_sent")]   public bool    MailSent   { get; set; }
    [JsonPropertyName("created_at")]  public string? CreatedAt  { get; set; }
}

/// <summary>补充说明请求体：凭证放在 body 里（与查询接口用 query 不同）。</summary>
public sealed class FeedbackReplyRequest
{
    [JsonPropertyName("content")] public string  Content { get; set; } = string.Empty;
    [JsonPropertyName("token")]   public string? Token   { get; set; }
    [JsonPropertyName("email")]   public string? Email   { get; set; }
}

public sealed class FeedbackReplyResult
{
    [JsonPropertyName("id")]      public long    Id      { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}
