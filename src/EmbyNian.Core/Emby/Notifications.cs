namespace EmbyNian.Emby;

/// <summary>
/// Emby 通知系统（服务器 4.8+ 的「用户通知」，设置页 <c>/settings/notifications.html</c> 那一套）的传输形状。
/// <para>
/// 2026-09-25 用户令「把本项目的通知改为 emby 的，让 emby 负责转发」：客户端不再自己向 MoviePilot 发 webhook，
/// 播放事件照旧经 <c>Sessions/Playing*</c> 报给 Emby，由服务器上装的通知服务（这台是官方 Webhooks 插件）转发出去；
/// 通知条目的增删改查走下面这几个形状。字段与这台 4.10.40 服务器实测一致，也核过官方 swagger 的
/// <c>Emby.Notifications.UserNotificationInfo</c>。
/// </para>
/// </summary>

/// <summary>装在服务器上的一个通知服务（通知渠道），来自 <c>GET Notifications/Services</c>。这台服务器上只有
/// 官方 Webhooks 插件一种（Id <c>webhooknotifications</c>）。</summary>
public sealed class NotificationServiceInfo
{
    /// <summary>官方 Webhooks 插件的服务键。本客户端对它提供完整编辑（地址、请求类型）；其余服务只编辑通用字段。</summary>
    public const string WebhooksKey = "webhooknotifications";

    /// <summary>显示名，如「Webhooks」，服务器已按用户的语言本地化。</summary>
    public string Name { get; set; } = "";

    /// <summary>服务键，如 <c>webhooknotifications</c>；建新条目时作为 <c>NotifierKey</c> 回传给 Defaults。</summary>
    public string Id { get; set; } = "";

    /// <summary>背后插件的全局 id；没有插件（纯内核服务）时缺省。</summary>
    public string? PluginId { get; set; }

    /// <summary>插件自带的设置编辑模块（官方网页端拿它载入每家服务的专属表单）。本客户端对 Webhooks 固定
    /// 自绘编辑器，别的服务只编辑通用字段，所以这个地址只存不载。</summary>
    public string? SetupModuleUrl { get; set; }

    /// <summary>图标名（如 <c>webhook</c>），官方网页端用它挑字形。本客户端不按它渲染。</summary>
    public string? Icon { get; set; }
}

/// <summary>一「类」通知事件（如「服务器」「媒体库」「播放」），来自 <c>GET Notifications/Types</c>。
/// 官方编辑器按类分节，每节一个全选框带下面一排子事件。</summary>
public sealed class NotificationCategoryInfo
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public List<NotificationTypeInfo> Events { get; set; } = [];
}

/// <summary>一个可订阅的通知事件（如「播放开始」<c>playback.start</c>）。名字服务器已本地化。</summary>
public sealed class NotificationTypeInfo
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string? CategoryName { get; set; }
    public string? CategoryId { get; set; }
}

/// <summary>
/// 一条通知配置（「往哪、谁、听哪些事件」），<c>Notifications/Services/Configured</c> 那三条路的请求与响应同形。
/// <para>
/// 官方网页端新建时先拿 <c>Defaults</c> 铺底，编辑器只覆写表单碰过的字段再整份 POST 回去 —— 本客户端照这个
/// 节奏：未知字段原样带回去，不猜默认值。条目按查询的用户（<c>UserId</c>）隔离；服务器自己还会给每条记一个
/// 内部的短 id（本机样例里是 <c>"1"</c>），那是服务器内部的用户号，与 <see cref="EmbyConnection.UserId"/> 的
/// 32 位号不同源，别拿来拼地址。
/// </para>
/// </summary>
public sealed class UserNotificationInfo
{
    /// <summary>服务键（同 <see cref="NotificationServiceInfo.Id"/>），决定这条通知由哪个渠道发出去。</summary>
    public string? NotifierKey { get; set; }

    /// <summary>同 <see cref="NotificationServiceInfo.SetupModuleUrl"/>，服务器回读时原样带来。</summary>
    public string? SetupModuleUrl { get; set; }

    /// <summary>服务显示名（如「Webhooks」），列表上在没有自定义名时用它。</summary>
    public string? ServiceName { get; set; }

    public string? PluginId { get; set; }

    /// <summary>这条通知的自定义名（官方编辑器里叫「名称」）。空时列表回落到 <see cref="ServiceName"/>。</summary>
    public string? FriendlyName { get; set; }

    /// <summary>条目 id（服务器生成）；新建条目时为空 —— 官方网页端正是拿它区分「新增」与「编辑」。</summary>
    public string? Id { get; set; }

    /// <summary>开关。官方编辑器没有这一档（新建即启用、删除才停），本客户端与它对齐：表单不放开关。</summary>
    public bool Enabled { get; set; }

    /// <summary>限定发往哪些用户（32 位用户号）。空表＝不按用户筛；只有管理员看得见这一栏。</summary>
    public List<string> UserIds { get; set; } = [];

    /// <summary>限定发往哪些设备（服务器给设备的数字号）。空表＝不按设备筛。</summary>
    public List<string> DeviceIds { get; set; } = [];

    /// <summary>限定哪些媒体库（条目的 Guid）。空表＝全部媒体库。</summary>
    public List<string> LibraryIds { get; set; } = [];

    /// <summary>订阅的事件 id 表（如 <c>playback.start</c>）。官方语义：一条通知订阅一个并集，不是「全订阅才发」。</summary>
    public List<string> EventIds { get; set; } = [];

    /// <summary>服务器内部记的用户号（本机样例 <c>"1"</c>），只透传不改写。</summary>
    public string? UserId { get; set; }

    /// <summary>官方语义未在本客户端用到的回读字段，整份存取时原样带回。</summary>
    public bool IsSelfNotification { get; set; }

    /// <summary>按剧集/专辑把同一部片的多集归并成一条（官方编辑器在「媒体库」类目下那一档）。</summary>
    public bool GroupItems { get; set; }

    /// <summary>
    /// 服务专属选项，官方 Webhooks 插件用 <c>Url</c>（目的地地址，MoviePilot 的地址里可能带
    /// <c>?token=</c> 令牌）和 <c>EnableMultipartFormData</c>（<c>"true"</c>/<c>"false"</c> 字符串，选
    /// 多部分表单还是简单表单提交）。字典值都是字符串 —— 这是服务器的形状，不是本客户端偷懒。
    /// </summary>
    public Dictionary<string, string> Options { get; set; } = [];

    /// <summary>
    /// 一份深拷贝。编辑器动的是副本：取消编辑不能把没保存的改动留在列表那一条上，而列表的重读是唯一把改动
    /// 「落屏」的路 —— 副本在 <c>Shell</c> 那边开编辑器前就换上，保存的才是用户改过的那一份。
    /// </summary>
    public UserNotificationInfo Clone() => new()
    {
        NotifierKey = NotifierKey,
        SetupModuleUrl = SetupModuleUrl,
        ServiceName = ServiceName,
        PluginId = PluginId,
        FriendlyName = FriendlyName,
        Id = Id,
        Enabled = Enabled,
        UserIds = [.. UserIds],
        DeviceIds = [.. DeviceIds],
        LibraryIds = [.. LibraryIds],
        EventIds = [.. EventIds],
        UserId = UserId,
        IsSelfNotification = IsSelfNotification,
        GroupItems = GroupItems,
        Options = new Dictionary<string, string>(Options, StringComparer.Ordinal)
    };
}

/// <summary>「限定用户」选择框里的一条：来自 <c>GET Users</c>。</summary>
public sealed class NotificationUser
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>「限定媒体库」选择框里的一条：来自 <c>GET Library/VirtualFolders</c>。官方编辑器认
/// <c>Guid</c>（<c>LibraryIds</c> 存的也是它），旧库没有 Guid 时回落到 <see cref="ItemId"/>。</summary>
public sealed class NotificationLibrary
{
    public string Name { get; set; } = "";
    public string? ItemId { get; set; }
    public string? Guid { get; set; }

    /// <summary>进 <see cref="UserNotificationInfo.LibraryIds"/> 的那个值。</summary>
    public string PickId => string.IsNullOrEmpty(Guid) ? ItemId ?? "" : Guid;
}

/// <summary>「限定设备」选择框里的一条：来自 <c>GET Devices</c>。</summary>
public sealed class NotificationDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? AppName { get; set; }
}

/// <summary><c>GET Devices</c> 的信封：<c>{ Items: [...], TotalRecordCount: n }</c>。</summary>
public sealed class NotificationDeviceList
{
    public List<NotificationDevice> Items { get; set; } = [];
}
