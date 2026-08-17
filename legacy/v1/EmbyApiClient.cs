using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EmbyMpvClient;

internal sealed class EmbyApiClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly AppSettings _settings;

    public EmbyApiClient(AppSettings settings)
    {
        _settings = settings;
        var serverUrl = settings.ServerUrl.Trim().TrimEnd('/');
        if (serverUrl.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) serverUrl = serverUrl[..^5];
        _http.BaseAddress = new Uri(serverUrl + "/emby/", UriKind.Absolute);
    }

    public async Task LoginAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(_settings.Username))
            throw new InvalidOperationException("请输入 Emby 用户名");

        ConfigureHeaders(false);
        using var response = await _http.PostAsJsonAsync("Users/AuthenticateByName", new LoginRequest
        {
            Username = _settings.Username.Trim(),
            Password = password
        });
        if (!response.IsSuccessStatusCode)
        {
            var details = (await response.Content.ReadAsStringAsync()).Trim();
            var message = $"Emby 登录失败：{(int)response.StatusCode} {response.ReasonPhrase}";
            if (!string.IsNullOrWhiteSpace(details)) message += Environment.NewLine + details;
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>() ?? throw new InvalidDataException("登录响应为空");
        if (string.IsNullOrWhiteSpace(auth.AccessToken) || string.IsNullOrWhiteSpace(auth.User?.Id))
            throw new InvalidDataException("Emby 登录响应中缺少访问令牌或用户 ID");
        _settings.AccessToken = auth.AccessToken;
        _settings.UserId = auth.User.Id;
        _settings.Save();
    }

    public async Task<List<EmbyItem>> GetViewsAsync()
    {
        ConfigureHeaders();
        var data = await _http.GetFromJsonAsync<ItemsResponse>($"Users/{_settings.UserId}/Views");
        return data?.Items ?? [];
    }

    public async Task<List<EmbyItem>> GetItemsAsync(string? parentId = null, string? search = null)
    {
        ConfigureHeaders();
        var args = new List<string>
        {
            "Recursive=false",
            "Fields=PrimaryImageAspectRatio,Overview,ProductionYear,RunTimeTicks",
            "SortBy=SortName",
            "SortOrder=Ascending",
            "Limit=300"
        };
        if (!string.IsNullOrWhiteSpace(parentId)) args.Add("ParentId=" + Uri.EscapeDataString(parentId));
        if (!string.IsNullOrWhiteSpace(search))
        {
            args[0] = "Recursive=true";
            args.Add("SearchTerm=" + Uri.EscapeDataString(search));
            args.Add("IncludeItemTypes=Movie,Series,Episode,Video,MusicVideo");
        }
        var data = await _http.GetFromJsonAsync<ItemsResponse>($"Users/{_settings.UserId}/Items?{string.Join('&', args)}");
        return data?.Items ?? [];
    }

    public string GetStreamUrl(EmbyItem item) =>
        $"{_settings.ServerUrl.TrimEnd('/')}/Videos/{item.Id}/stream?Static=true&api_key={Uri.EscapeDataString(_settings.AccessToken)}";

    public string GetImageUrl(EmbyItem item, int width = 400) =>
        $"{_settings.ServerUrl.TrimEnd('/')}/Items/{item.Id}/Images/Primary?maxWidth={width}&quality=90&api_key={Uri.EscapeDataString(_settings.AccessToken)}";

    public async Task<byte[]> GetImageAsync(EmbyItem item, int width = 360, CancellationToken cancellationToken = default)
    {
        ConfigureHeaders();
        return await _http.GetByteArrayAsync(GetImageUrl(item, width), cancellationToken);
    }

    private void ConfigureHeaders(bool authenticated = true)
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Authorization",
            $"MediaBrowser Client=\"Emby MPV Client\", Device=\"Windows\", DeviceId=\"{Environment.MachineName}-EmbyMpvClient\", Version=\"1.0.0\"");
        if (authenticated && !string.IsNullOrWhiteSpace(_settings.AccessToken))
            _http.DefaultRequestHeaders.Add("X-Emby-Token", _settings.AccessToken);
    }

    public void Dispose() => _http.Dispose();

    private sealed class AuthResponse
    {
        public string AccessToken { get; set; } = "";
        public EmbyUser? User { get; set; }
    }

    private sealed class EmbyUser { public string Id { get; set; } = ""; }
    private sealed class ItemsResponse { public List<EmbyItem> Items { get; set; } = []; }

    private sealed class LoginRequest
    {
        [JsonPropertyName("Username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("Pw")]
        public string Password { get; set; } = "";
    }
}

internal sealed class EmbyItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Overview { get; set; }
    public int? ProductionYear { get; set; }
    public long? RunTimeTicks { get; set; }

    [JsonIgnore]
    public string DisplayType => Type switch
    {
        "Movie" => "电影",
        "Series" => "剧集",
        "Season" => "季",
        "Episode" => "单集",
        "Folder" => "文件夹",
        "CollectionFolder" => "媒体库",
        _ => Type
    };
}
