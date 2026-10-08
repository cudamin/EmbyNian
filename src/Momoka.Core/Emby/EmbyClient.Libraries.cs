using System.Globalization;
using System.Text.Json.Nodes;

namespace Momoka.Emby;

public sealed partial class EmbyClient
{
    public async Task<IReadOnlyList<EmbyVirtualFolder>> GetVirtualFoldersAsync(CancellationToken token)
    {
        var folders = new List<EmbyVirtualFolder>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; ;)
        {
            var result = await http.GetJsonAsync<JsonObject>(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders/Query",
                ("StartIndex", start.ToString(CultureInfo.InvariantCulture)), ("Limit", "200")), Context, token).ConfigureAwait(false);
            var items = result["Items"] as JsonArray ?? throw new InvalidDataException("服务器没有返回媒体库列表。");
            if (items.Any(item => item is not JsonObject)) throw new InvalidDataException("媒体库分页包含无效条目。");
            foreach (var item in items.OfType<JsonObject>())
            {
                var folder = new EmbyVirtualFolder(item);
                if (folder.Id.Length == 0 || !ids.Add(folder.Id)) throw new InvalidDataException("媒体库分页包含重复或无效的条目。");
                folders.Add(folder);
            }
            start += items.Count;
            var total = int.TryParse(result["TotalRecordCount"]?.ToString(), out var count) ? count : start;
            if (start >= total) return folders;
            if (items.Count == 0) throw new InvalidDataException("媒体库列表尚未读取完整，请重试。");
        }
    }

    public Task<JsonObject> GetLibraryAvailableOptionsAsync(string contentType, bool isNew, CancellationToken token) =>
        http.GetJsonAsync<JsonObject>(EmbyUrl.Combine(ApiBase, "Libraries/AvailableOptions",
            ("LibraryContentType", contentType), ("IsNewLibrary", isNew ? "true" : "false")), Context, token);

    public Task<JsonArray> GetLibraryCulturesAsync(CancellationToken token) =>
        http.GetJsonAsync<JsonArray>(EmbyUrl.Combine(ApiBase, "Localization/Cultures"), Context, token);

    public Task<JsonArray> GetLibraryCountriesAsync(CancellationToken token) =>
        http.GetJsonAsync<JsonArray>(EmbyUrl.Combine(ApiBase, "Localization/Countries"), Context, token);

    public Task CreateVirtualFolderAsync(string name, string contentType, JsonObject options, CancellationToken token)
    {
        EmbyLibraryOptions.ValidateNew(name, options);
        return http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders",
            ("name", name.Trim()), ("collectionType", string.IsNullOrEmpty(contentType) ? null : contentType),
            ("refreshLibrary", "true")), new JsonObject { ["LibraryOptions"] = options.DeepClone() }, Context, token);
    }

    public Task SaveVirtualFolderOptionsAsync(string id, JsonObject options, CancellationToken token) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders/LibraryOptions"),
            new JsonObject { ["Id"] = LibraryId(id), ["LibraryOptions"] = options.DeepClone() }, Context, token);

    public Task RenameVirtualFolderAsync(string id, string name, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders/Name"),
            new { Id = LibraryId(id), NewName = name.Trim() }, Context, token);
    }

    public Task RemoveVirtualFolderAsync(string id, CancellationToken token) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders/Delete"), new { Id = LibraryId(id) }, Context, token);

    public Task AddLibraryPathAsync(string id, JsonObject pathInfo, CancellationToken token) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders/Paths", ("refreshLibrary", "true")),
            new JsonObject { ["Id"] = LibraryId(id), ["PathInfo"] = pathInfo.DeepClone() }, Context, token);

    public Task UpdateLibraryPathAsync(string id, JsonObject pathInfo, CancellationToken token) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders/Paths/Update"),
            new JsonObject { ["Id"] = LibraryId(id), ["PathInfo"] = pathInfo.DeepClone() }, Context, token);

    public Task RemoveLibraryPathAsync(string id, string path, CancellationToken token) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, "Library/VirtualFolders/Paths/Delete"),
            new { Id = LibraryId(id), Path = path, RefreshLibrary = true }, Context, token);

    public Task<JsonArray> GetLibraryTasksAsync(CancellationToken token) =>
        http.GetJsonAsync<JsonArray>(EmbyUrl.Combine(ApiBase, "ScheduledTasks"), Context, token);

    public Task SetLibraryTaskRunningAsync(string id, bool running, CancellationToken token) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, $"ScheduledTasks/Running/{LibraryId(id)}" + (running ? "" : "/Delete")), null, Context, token);

    public Task ScanVirtualFolderAsync(string id, string mode, bool replaceImages, CancellationToken token, bool replaceThumbnails = false)
    {
        if (mode is not ("scan" or "missing" or "all")) throw new ArgumentException("未知的媒体库扫描模式。");
        return http.PostAsync(EmbyUrl.Combine(ApiBase, $"Items/{LibraryId(id)}/Refresh",
            ("Recursive", "true"), ("MetadataRefreshMode", mode == "scan" ? "Default" : "FullRefresh"),
            ("ImageRefreshMode", mode == "scan" ? "Default" : "FullRefresh"),
            ("ReplaceAllMetadata", mode == "all" ? "true" : "false"),
            ("ReplaceAllImages", mode != "scan" && replaceImages ? "true" : "false"),
            ("ReplaceThumbnailImages", mode != "scan" && replaceThumbnails ? "true" : "false")), null, Context, token);
    }

    public Task<JsonObject> GetLibraryNamedConfigurationAsync(string key, CancellationToken token) =>
        http.GetJsonAsync<JsonObject>(EmbyUrl.Combine(ApiBase, $"System/Configuration/{LibraryId(key)}"), Context, token);

    public Task SaveLibraryNamedConfigurationAsync(string key, JsonObject configuration, CancellationToken token) =>
        http.PostAsync(EmbyUrl.Combine(ApiBase, $"System/Configuration/{LibraryId(key)}"), configuration, Context, token);

    public Task<JsonObject> GetServerDirectoryBrowserAsync(CancellationToken token) =>
        http.GetJsonAsync<JsonObject>(EmbyUrl.Combine(ApiBase, "Environment/DefaultDirectoryBrowser"), Context, token);

    public async Task<string> GetServerParentDirectoryAsync(string path, CancellationToken token) =>
        string.IsNullOrEmpty(path) ? "" : System.Text.Encoding.UTF8.GetString(await http.GetBytesAsync(
            EmbyUrl.Combine(ApiBase, "Environment/ParentPath", ("Path", path)), Context, token).ConfigureAwait(false));

    public Task<JsonArray> GetServerDirectoriesAsync(string path, string username, string password, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path)) return http.GetJsonAsync<JsonArray>(EmbyUrl.Combine(ApiBase, "Environment/Drives"), Context, token);
        if (path == "Network") return http.GetJsonAsync<JsonArray>(EmbyUrl.Combine(ApiBase, "Environment/NetworkDevices"), Context, token);
        return http.PostJsonAsync<JsonArray>(EmbyUrl.Combine(ApiBase, "Environment/DirectoryContents",
            ("Path", path), ("IncludeFiles", "false"), ("IncludeDirectories", "true")), new { Username = username, Password = password }, Context, token);
    }

    public Task ValidateServerDirectoryAsync(string path, string username, string password, bool writable, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return http.PostAsync(EmbyUrl.Combine(ApiBase, "Environment/ValidatePath", ("Path", path)),
            new { ValidateWriteable = writable, IsFile = false, Username = username, Password = password }, Context, token);
    }

    private static string LibraryId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id is "." or ".." || id.IndexOfAny(['/', '\\', '?', '#']) >= 0) throw new ArgumentException("媒体库或任务标识无效。");
        return Uri.EscapeDataString(id);
    }
}
