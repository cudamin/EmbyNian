using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using EmbyNian.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.ViewModels;

public sealed partial class ServerLibrariesViewModel
{
    private async Task LoadLocalizationAsync(CancellationToken token)
    {
        if (Languages.Count > 0 && Countries.Count > 0) return;
        var cultures = _scope!.ExecuteAsync((client, ct) => client.GetLibraryCulturesAsync(ct), token);
        var countries = _scope.ExecuteAsync((client, ct) => client.GetLibraryCountriesAsync(ct), token);
        await Task.WhenAll(cultures, countries);
        EnsureCurrent(token);
        Languages.Clear(); Countries.Clear();
        Languages.Add(new("", "默认")); Countries.Add(new("", "默认"));
        foreach (var item in (await cultures).OfType<JsonObject>().DistinctBy(item => EmbyLibraryOptions.Text(item, "TwoLetterISOLanguageName")))
            Languages.Add(new(EmbyLibraryOptions.Text(item, "TwoLetterISOLanguageName"), EmbyLibraryOptions.Text(item, "DisplayName")));
        foreach (var item in (await countries).OfType<JsonObject>())
            Countries.Add(new(EmbyLibraryOptions.Text(item, "TwoLetterISORegionName"), EmbyLibraryOptions.Text(item, "DisplayName")));
    }

    private void BuildEditor()
    {
        Sections.Clear(); Providers.Clear(); ImageSections.Clear();
        foreach (var group in EmbyLibrarySettings.All.Where(setting => setting.AppliesTo(CurrentType)).GroupBy(setting => setting.Group))
        {
            var section = new LibraryFieldSection(group.Key);
            foreach (var setting in group)
            {
                var choices = ChoicesFor(setting);
                if (setting.Key == "MultiVersion" && CurrentType is "homevideos" or "musicvideos") choices = choices.Where(choice => choice.Value is "files" or "none");
                if (setting.Key == "MusicFolderStructure" && CurrentType == "audiobooks") choices = [new("", "其他或非结构化"), new("artist_album_track", "作者 / 书籍 / 章节"), new("album_track", "书籍 / 章节")];
                section.Fields.Add(new(setting, _draft, choices, EditorChanged));
            }
            Sections.Add(section);
        }
        AddProviders("元数据读取器", "readers", _available, _draft, "MetadataReaders", "DisabledLocalMetadataReaders", "LocalMetadataReaderOrder", true);
        if (CurrentType != "boxsets") AddProviders("元数据存储格式", "savers", _available, _draft, "MetadataSavers", "MetadataSavers", null, false);
        foreach (var type in (_available["TypeOptions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var typeName = EmbyLibraryOptions.Text(type, "Type");
            var target = EmbyLibraryOptions.TypeOptions(_draft, typeName);
            AddProviders(EmbyLibraryOptions.TypeName(typeName) + " · 元数据下载器", "metadata", type, target, "MetadataFetchers", "MetadataFetchers", "MetadataFetcherOrder", false);
            AddProviders(EmbyLibraryOptions.TypeName(typeName) + " · 图片获取器", "images", type, target, "ImageFetchers", "ImageFetchers", "ImageFetcherOrder", false);
            BuildImageOptions(typeName, type, target);
        }
        AddProviders("字幕下载器", "subtitles", _available, _draft, "SubtitleFetchers", "DisabledSubtitleFetchers", "SubtitleFetcherOrder", true);
        AddProviders("歌词下载器", "lyrics", _available, _draft, "LyricsFetchers", "DisabledLyricsFetchers", "LyricsFetcherOrder", true);
        // 构建仅补足展示所需的默认形状，它们不应被当作用户改动提交。
        _baseline = (JsonObject)_draft.DeepClone();
        RefreshFieldVisibility();
    }

    private IEnumerable<LibraryChoice> ChoicesFor(LibrarySetting setting) => setting.Kind switch
    {
        "language" => Languages,
        "languages" => Languages.Where(choice => choice.Value.Length > 0),
        "country" => Countries,
        _ => setting.Choices ?? []
    };

    private void AddProviders(string name, string kind, JsonObject available, JsonObject target, string availableKey, string selectionKey, string? orderKey, bool disabledList)
    {
        if (available[availableKey] is not JsonArray { Count: > 0 } providers) return;
        Providers.Add(new(name, kind, providers, target, selectionKey, orderKey, disabledList, EditorChanged));
    }

    private void BuildImageOptions(string typeName, JsonObject availableType, JsonObject target)
    {
        var supported = EmbyLibraryOptions.Strings(availableType, "SupportedImageTypes");
        if (supported.Length == 0 || availableType["ImageFetchers"] is not JsonArray { Count: > 0 }) return;
        var section = new LibraryFieldSection(EmbyLibraryOptions.TypeName(typeName) + " · 图片下载设置");
        foreach (var imageType in supported)
        {
            var original = (target["ImageOptions"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => EmbyLibraryOptions.Text(item, "Type") == imageType);
            var defaults = (availableType["DefaultImageOptions"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => EmbyLibraryOptions.Text(item, "Type") == imageType);
            if (original is null)
            {
                original = EmbyLibraryOptions.TypeOptions(target, imageType, "ImageOptions");
                original["Limit"] = EmbyLibraryOptions.Number(defaults ?? new(), "Limit", imageType == "Primary" ? 1 : 0);
                original["MinWidth"] = EmbyLibraryOptions.Number(defaults ?? new(), "MinWidth");
            }
            var imageOptions = original;
            if (imageType == "Backdrop")
            {
                section.Fields.Add(new(new("Limit", section.Name, "每个项目的最大背景图数量", "numberchoice", "0", Maximum: 20), imageOptions,
                    Enumerable.Range(0, 21).Select(number => new LibraryChoice(number.ToString(CultureInfo.InvariantCulture), number.ToString(CultureInfo.InvariantCulture))), EditorChanged));
                section.Fields.Add(new(new("MinWidth", section.Name, "背景图最小下载宽度", "numberchoice", "0"), imageOptions,
                    new[] { 0, 480, 720, 1280, 1920 }.Select(number => new LibraryChoice(number.ToString(CultureInfo.InvariantCulture), number == 0 ? "不限" : number + " 像素")), EditorChanged));
            }
            else
            {
                var label = imageType switch { "Primary" => "主图", "Art" => "艺术图", "Banner" => "横幅", "Box" => "封面", "BoxRear" => "封底", "Disc" => "光盘", "Logo" => "标志", "Menu" => "菜单", "Thumb" => "缩略图", _ => imageType };
                section.Fields.Add(new(new("Limit", section.Name, label), imageOptions, [], EditorChanged,
                    value => imageOptions["Limit"] = bool.Parse(value) ? 1 : 0, (EmbyLibraryOptions.Number(imageOptions, "Limit") > 0).ToString(CultureInfo.InvariantCulture)));
            }
        }
        ImageSections.Add(section);
    }

    private void EditorChanged()
    {
        if (_building) return;
        HasChanges = true;
        RefreshFieldVisibility();
    }

    private void RefreshFieldVisibility()
    {
        var metadata = Providers.Any(group => group.Kind == "metadata");
        var images = Providers.Any(group => group.Kind == "images");
        var collectionDownloader = Providers.Where(group => group.Kind == "metadata").SelectMany(group => group.Providers).Any(provider => provider.Enabled && provider.Features.Contains("Collections"));
        var collectionReader = Providers.Where(group => group.Kind == "readers").SelectMany(group => group.Providers).Any(provider => provider.Enabled && provider.Features.Contains("Collections"));
        var adult = Providers.Where(group => group.Kind == "metadata").SelectMany(group => group.Providers).Any(provider => provider.Enabled && provider.Features.Contains("Adult"));
        foreach (var section in Sections)
        {
            foreach (var field in section.Fields)
                field.Visibility = Show(field.Setting.Dependency switch
                {
                    "metadata" => metadata,
                    "images" => images,
                    "collections" => collectionDownloader,
                    "collectionsize" => collectionReader || collectionDownloader && EmbyLibraryOptions.Flag(_draft, "ImportCollections"),
                    "adult" => adult,
                    "windows" => _system?.OperatingSystem == "Windows",
                    "chapters" => EmbyLibraryOptions.Flag(_draft, "AutoGenerateChapters"),
                    "thumbnails" => EmbyLibraryOptions.Flag(_draft, "EnableChapterImageExtraction"),
                    "thumbnailsets" => EmbyLibraryOptions.Flag(_draft, "EnableChapterImageExtraction") && EmbyLibraryOptions.Number(_draft, "ThumbnailImagesIntervalSeconds", 10) != -1,
                    "subtitles" => Providers.Any(group => group.Kind == "subtitles"),
                    "lyrics" => Providers.Any(group => group.Kind == "lyrics"),
                    _ => true
                });
            section.Visibility = Show(section.Fields.Any(field => field.Visibility == Visibility.Visible));
        }
    }

    internal async Task OpenAdvancedAsync()
    {
        if (!CanUse || _scope is not { } scope) return;
        Tab = "advanced";
        if (_advancedLoaded) return;
        await RunAsync("读取高级设置失败", async token =>
        {
            await LoadLocalizationAsync(token);
            var configuration = await scope.ExecuteAsync((client, ct) => client.GetServerConfigurationAsync(ct), token);
            var metadata = await scope.ExecuteAsync((client, ct) => client.GetLibraryNamedConfigurationAsync("metadata", ct), token);
            EnsureCurrent(token);
            _building = true;
            _configuration = (JsonObject)configuration.DeepClone();
            _metadata = (JsonObject)metadata.DeepClone();
            if (EmbyLibraryOptions.Text(_configuration, "MetadataPath").Length == 0) _configuration["MetadataPath"] = _system?.InternalMetadataPath ?? "";
            _configurationBaseline = (JsonObject)_configuration.DeepClone();
            _metadataBaseline = (JsonObject)_metadata.DeepClone();
            BuildAdvanced();
            _advancedLoaded = true;
            _building = false;
            HasChanges = false;
            Announce();
        });
    }

    private void BuildAdvanced()
    {
        AdvancedSections.Clear();
        foreach (var group in EmbyLibrarySettings.Advanced.GroupBy(setting => setting.Group))
        {
            var section = new LibraryFieldSection(group.Key);
            foreach (var setting in group)
                section.Fields.Add(new(setting, setting.Key == "UseFileCreationTimeForDateAdded" ? _metadata : _configuration, ChoicesFor(setting), () => { if (!_building) HasChanges = true; }));
            AdvancedSections.Add(section);
        }
    }

    internal async Task<bool> SetMetadataDirectoryAsync(string path, string networkPath)
    {
        if (!CanSaveAdvanced || _scope is not { } scope) return false;
        var success = false;
        await RunAsync("元数据目录无法写入", async token =>
        {
            await scope.ExecuteAsync((client, ct) => client.ValidateServerDirectoryAsync(path, "", "", true, ct), token);
            EnsureCurrent(token);
            _configuration["MetadataPath"] = path;
            _configuration["MetadataNetworkPath"] = networkPath;
            _building = true;
            BuildAdvanced();
            _building = false;
            HasChanges = true;
            success = true;
        });
        return success;
    }

    [RelayCommand(CanExecute = nameof(CanSaveAdvanced))]
    private async Task SaveAdvancedAsync()
    {
        if (!CanSaveAdvanced || _scope is not { } scope) return;
        await RunAsync("高级设置未全部保存，请重试", async token =>
        {
            ValidateFields(AdvancedSections);
            if (!JsonNode.DeepEquals(_configuration["MetadataPath"], _configurationBaseline["MetadataPath"]) && EmbyLibraryOptions.Text(_configuration, "MetadataPath").Length > 0)
                await scope.ExecuteAsync((client, ct) => client.ValidateServerDirectoryAsync(EmbyLibraryOptions.Text(_configuration, "MetadataPath"), "", "", true, ct), token);
            var latest = await scope.ExecuteAsync((client, ct) => client.GetServerConfigurationAsync(ct), token);
            EnsureCurrent(token);
            var merged = EmbyLibraryOptions.Merge(latest, _configurationBaseline, _configuration);
            if (!JsonNode.DeepEquals(latest, merged)) await scope.ExecuteAsync((client, ct) => client.SaveServerConfigurationAsync(merged, ct), token);
            EnsureCurrent(token);
            _configurationBaseline = (JsonObject)_configuration.DeepClone();
            var metadata = await scope.ExecuteAsync((client, ct) => client.GetLibraryNamedConfigurationAsync("metadata", ct), token);
            EnsureCurrent(token);
            var mergedMetadata = EmbyLibraryOptions.Merge(metadata, _metadataBaseline, _metadata);
            if (!JsonNode.DeepEquals(metadata, mergedMetadata)) await scope.ExecuteAsync((client, ct) => client.SaveLibraryNamedConfigurationAsync("metadata", mergedMetadata, ct), token);
            EnsureCurrent(token);
            _metadataBaseline = (JsonObject)_metadata.DeepClone();
            HasChanges = false;
            Notify("高级设置已保存", null, InfoBarSeverity.Success);
        });
    }
}
