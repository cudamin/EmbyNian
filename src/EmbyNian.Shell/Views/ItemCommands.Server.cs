using EmbyNian.Diagnostics;
using EmbyNian.Emby;
using EmbyNian.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyNian.Shell.Views;

/// <summary>
/// 「更多」菜单上会碰服务器的那几条 —— 合集、下载、封面图、字幕、刮削与刷新、扫库、从继续观看中移除、删除。
/// <para>
/// 和菜单本身分成两个文件，只为一件事：一个文件还能从头读到尾。哪几条出现在菜单上是
/// <see cref="ItemMenu"/> 的事，搭菜单是 <c>ItemCommands.cs</c> 的事，这里是每一条按下去之后真正发生的事。
/// </para>
/// <para>
/// 每一条都长一个样子：<see cref="GuardAsync"/> 包住，出错就写一行日志、在提示条上说一句人话；改不动手上这张
/// 卡片的（改了封面、删掉了条目、从继续观看里拿掉了）喊一声 <c>changed</c> 让整页重读。
/// </para>
/// </summary>
internal static partial class ItemCommands
{
    /// <summary>
    /// 下载排成一队，一次只下一个。
    /// <para>
    /// 十几个 G 的文件同时下四个，只会让四个都慢，而提示条上那一行读数还会互相盖 —— 那一行一次只说得清一件事。
    /// 静态的：队列是这台机器的，跟哪一页发起的无关。
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    /// <summary>
    /// 这几条命令共用的外壳：出错写日志加一句提示，取消和「窗口已经没了」两种照旧不出声。
    /// <para>
    /// 一处而不是八处 —— 八份一模一样的 try/catch 里，迟早有一份漏掉最后那句 <see cref="IShellActions.Notify"/>，
    /// 而那种漏法的症状是「点了没反应」，最难查。
    /// </para>
    /// </summary>
    private static async Task GuardAsync(IShellActions shell, string failure, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            Log.Warn(Category, failure, error);
            shell.Notify($"{failure}：{Failure.Describe(error)}", InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// 一次请求。每一条命令都是「问服务器一次」，取消令牌一律给 <see cref="CancellationToken.None"/>：菜单点下去
    /// 之后翻到别的页，这一趟照样该走完 —— 用户要的是那件事做成，不是那一页还在。
    /// </summary>
    private static Task<T> AskAsync<T>(EmbySession session, Func<EmbyClient, CancellationToken, Task<T>> work) =>
        session.ExecuteAsync(work, CancellationToken.None);

    /// <inheritdoc cref="AskAsync{T}"/>
    private static Task TellAsync(EmbySession session, Func<EmbyClient, CancellationToken, Task> work) =>
        session.ExecuteAsync(work, CancellationToken.None);

    /// <summary>
    /// 弹对话框要的那个根。取不到就什么都不做 —— 这一句只在窗口已经关掉的路上成立，那时候也没有人在等这张表。
    /// </summary>
    private static XamlRoot? Root(FrameworkElement owner) => owner.XamlRoot;

    /// <summary>打开所属剧集。手上只有剧集 id，所以先按 id 问回那个条目再交给外壳。</summary>
    private static void OpenSeries(EmbySession session, IShellActions shell, EmbyItem item)
    {
        if (item.SeriesId is not { Length: > 0 } seriesId) return;

        _ = GuardAsync(shell, "打开剧集失败", async () =>
        {
            var series = await AskAsync(session, (client, token) => client.GetItemAsync(seriesId, token))
                .ConfigureAwait(true);
            shell.OpenItem(series);
        });
    }

    /// <summary>
    /// 编辑元数据. Reads the item back before showing the form — the grid's copy came from a browse query
    /// with a limited field set, so editing from it would offer a blank 简介 and then save that blank
    /// over the real one.
    /// </summary>
    private static Task EditAsync(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        Action? changed) =>
        GuardAsync(shell, "编辑元数据失败", async () =>
        {
            if (Root(owner) is not { } root) return;

            var item = card.Item;
            var json = await AskAsync(session, (client, token) => client.GetItemJsonAsync(item.Id, token))
                .ConfigureAwait(true);

            var dialog = new MetadataDialog(item, ItemMetadataEdit.Read(json)) { XamlRoot = root };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            // Nothing to send is a success, not a no-op to report: the user opened the form, looked, and
            // pressed 保存. Saying 「已保存」 for a request that never went out would be a lie, and an
            // error for it would be nonsense.
            if (!dialog.Edit.ApplyTo(json))
            {
                Log.Debug(Category, $"编辑元数据：{item.Name} 没有改动");
                return;
            }

            await TellAsync(session, (client, token) => client.UpdateItemAsync(item.Id, json, token))
                .ConfigureAwait(true);

            shell.Notify($"已保存「{dialog.Edit.Name}」的元数据");

            // A metadata save can change the title, the sort position and the artwork at once, and no
            // amount of patching one card covers that.
            changed?.Invoke();
        });

    /// <summary>
    /// 添加到合集：列出服务器上现有的合集让人挑一个，或者当场新建一个。
    /// <para>
    /// 已经在那个合集里的条目再加一次，服务器自己忽略，所以这里不必先查一遍「在不在里头」—— 那是一趟请求换一句
    /// 没人需要的提示。
    /// </para>
    /// </summary>
    private static Task CollectAsync(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card) =>
        GuardAsync(shell, "添加到合集失败", async () =>
        {
            if (Root(owner) is not { } root) return;

            var item = card.Item;
            var collections = await AskAsync(session, (client, token) => client.GetCollectionsAsync(token))
                .ConfigureAwait(true);

            var dialog = new CollectionDialog(item, collections) { XamlRoot = root };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            if (dialog.Chosen is { } existing)
            {
                await TellAsync(session, (client, token) =>
                        client.AddToCollectionAsync(existing.Id, [item.Id], token))
                    .ConfigureAwait(true);

                shell.Notify($"已把「{item.Name}」加入合集「{existing.Name}」");
                return;
            }

            if (dialog.NewName is not { Length: > 0 } name) return;

            var created = await AskAsync(session, (client, token) =>
                    client.CreateCollectionAsync(name, [item.Id], token))
                .ConfigureAwait(true);

            shell.Notify($"已新建合集「{created.Name ?? name}」，并放入「{item.Name}」");
        });

    /// <summary>
    /// 下载到设备。落点是写死的一个文件夹（<see cref="DownloadPlan"/>），提示条上报进度，下完那一句里带着路径。
    /// <para>
    /// 一部剧或者一季会先问回它的单集列表，一个个下进同一个子文件夹；一个文件就是一趟。整队排在
    /// <see cref="DownloadGate"/> 后面。
    /// </para>
    /// </summary>
    private static Task DownloadAsync(EmbySession session, IShellActions shell, CardItem card) =>
        GuardAsync(shell, "下载失败", async () =>
        {
            var item = card.Item;
            var files = await FilesOfAsync(session, item).ConfigureAwait(true);

            if (files.Count == 0)
            {
                shell.Notify($"服务器上找不到「{item.Name}」可下载的文件", InfoBarSeverity.Warning);
                return;
            }

            var folder = DownloadPlan.Folder(DownloadPlan.DefaultRoot, item);

            // 前面还有一个在下：先说一句，不然点下去要等上十几分钟才有动静。
            if (DownloadGate.CurrentCount == 0) shell.Notify($"「{item.Name}」已排进下载队列");

            await DownloadGate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                await SaveAsync(session, shell, item, files, folder).ConfigureAwait(true);
            }
            finally
            {
                DownloadGate.Release();
            }
        });

    /// <summary>
    /// 这个条目背后有哪几个文件。一个文件的条目要重新按 id 问一遍 —— 卡片那一份来自列表接口，不带媒体源，而
    /// 后缀名只有媒体源说得出来。
    /// <para>
    /// 一叠单集这里<b>不筛</b>：单集列表带不带媒体源要看服务器的版本（自检里印着这台答的是几分之几），带的话
    /// 一趟就够，不带的话由 <see cref="SaveAsync"/> 在下每一个之前补问一次。在这里筛掉的下场是「找不到可下载的
    /// 文件」—— 明明二十四集都在。
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<EmbyItem>> FilesOfAsync(EmbySession session, EmbyItem item)
    {
        if (!ItemMenu.IsEpisodeSet(item))
        {
            var full = await AskAsync(session, (client, token) =>
                    client.GetItemAsync(item.Id, token, EmbyFields.Files))
                .ConfigureAwait(true);

            return full.DefaultMediaSource is null ? [] : [full];
        }

        // 剧用自己的 id，季用它所属剧的 id 加上自己这一季 —— 单集列表那个接口只认剧。
        var seriesId = item.Type == EmbyItemType.Series ? item.Id : item.SeriesId;
        if (seriesId is not { Length: > 0 } show) return [];

        return await AskAsync(session, (client, token) => client.GetEpisodesAsync(
                show,
                item.Type == EmbyItemType.Season ? item.Id : null,
                token,
                EmbyFields.Files))
            .ConfigureAwait(true);
    }

    /// <summary>一个个存下来，边下边在提示条上报进度。</summary>
    private static async Task SaveAsync(
        EmbySession session,
        IShellActions shell,
        EmbyItem item,
        IReadOnlyList<EmbyItem> files,
        string folder)
    {
        var index = 0;
        var saved = 0;
        var skipped = 0;
        var last = "";

        foreach (var file in files)
        {
            index++;

            // 手上这一份没带媒体源就补问一次：后缀名只有它说得出来，而没有后缀的影片双击打不开。
            var source = file.DefaultMediaSource;
            if (source is null)
            {
                var full = await AskAsync(session, (client, token) =>
                        client.GetItemAsync(file.Id, token, EmbyFields.Files))
                    .ConfigureAwait(true);

                source = full.DefaultMediaSource;
            }

            if (source is null)
            {
                skipped++;
                continue;
            }

            var path = Path.Combine(folder, DownloadPlan.FileName(file, source));
            var head = files.Count > 1
                ? $"「{item.Name}」第 {index}/{files.Count} 个"
                : $"「{file.Name}」";

            // Progress<T> 在界面线程上造出来，所以回调自己会回到界面线程 —— 下载那一头跑在线程池上。
            var progress = new Progress<(long Done, long? Total)>(state =>
                shell.Notify($"正在下载 {head}{Portion(state)}"));

            shell.Notify($"正在下载 {head}…");

            await TellAsync(session, (client, token) => client.DownloadToFileAsync(file.Id, path, progress, token))
                .ConfigureAwait(true);

            saved++;
            last = path;
        }

        // 最后那一句要说清落在哪儿：这是用户唯一一次看得到完整路径的机会。
        shell.Notify(saved switch
        {
            0 => $"「{item.Name}」没有一个文件下得下来（服务器上都没有媒体源）",
            1 when files.Count == 1 => $"已下载到 {last}",
            _ => $"已下载 {saved} 个文件到 {folder}" + (skipped > 0 ? $"（{skipped} 个跳过：服务器上没有媒体源）" : "")
        }, saved == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
    }

    /// <summary>「45%（1.2 GB / 2.7 GB）」，服务器没说总长度时只报已经下了多少。</summary>
    private static string Portion((long Done, long? Total) state) =>
        state.Total is { } total and > 0
            ? $" {state.Done * 100 / total}%（{TimeFormat.FileSize(state.Done)} / {TimeFormat.FileSize(total)}）"
            : $" 已下载 {TimeFormat.FileSize(state.Done)}";

    /// <summary>
    /// 修改媒体封面图：把各家刮削源上这个条目的封面列出来挑一张，挑中的交给服务器去取。
    /// <para>
    /// 换完喊一声 <c>changed</c>：封面换了之后服务器上那个图片标签也变了，而缓存是按标签存的 —— 重读这一页
    /// 就会自己去取新的那张，不重读的话屏上还是旧封面，看着像没换成。
    /// </para>
    /// </summary>
    private static Task CoverAsync(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        Action? changed) =>
        GuardAsync(shell, "修改封面图失败", async () =>
        {
            if (Root(owner) is not { } root) return;

            var item = card.Item;
            var found = await AskAsync(session, (client, token) =>
                    client.GetRemoteImagesAsync(item.Id, EmbyImageStore.Primary, token))
                .ConfigureAwait(true);

            var dialog = new CoverDialog(
                item,
                found,
                url => AskAsync(session, (client, token) => client.GetRemoteImageBytesAsync(url, token)))
            {
                XamlRoot = root
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (dialog.Chosen is not { } chosen) return;

            await TellAsync(session, (client, token) => client.ApplyRemoteImageAsync(
                    item.Id,
                    EmbyImageStore.Primary,
                    chosen.Url,
                    chosen.ProviderName,
                    token))
                .ConfigureAwait(true);

            shell.Notify($"已更新「{item.Name}」的封面图");
            changed?.Invoke();
        });

    /// <summary>
    /// 搜索和修改字幕。先按 id 把这个条目重新问一遍 —— 要的是它的媒体源：搜字幕和挂字幕都得点名是哪一个文件，
    /// 而卡片那一份来自列表接口，不带媒体源。
    /// <para>
    /// 搜、下、删三件事由这张表自己按下按钮时调回来（三个委托），而不是「选好了再回来一次做」：一次打开可能
    /// 搜好几轮、删两条再下一条，把它拆成「对话框返回一个选择」的形状反而要在这里重放一遍那几步。
    /// </para>
    /// </summary>
    private static Task SubtitlesAsync(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        Action? changed) =>
        GuardAsync(shell, "字幕操作失败", async () =>
        {
            if (Root(owner) is not { } root) return;

            var item = card.Item;
            var full = await AskAsync(session, (client, token) =>
                    client.GetItemAsync(item.Id, token, EmbyFields.Files))
                .ConfigureAwait(true);

            if (full.DefaultMediaSource is not { } source)
            {
                shell.Notify($"服务器上找不到「{item.Name}」的媒体文件", InfoBarSeverity.Warning);
                return;
            }

            var dialog = new SubtitleDialog(
                full,
                source,
                language => AskAsync(session, (client, token) =>
                    client.SearchSubtitlesAsync(full.Id, source.Id, language, token)),
                subtitle => TellAsync(session, (client, token) =>
                    client.DownloadSubtitleAsync(full.Id, source.Id, subtitle.Id, token)),
                stream => TellAsync(session, (client, token) =>
                    client.DeleteSubtitleAsync(full.Id, stream.Index, token)))
            {
                XamlRoot = root
            };

            await dialog.ShowAsync();

            // 这一页上「字幕」那个下拉列的是这个文件的轨道，动过就得重读一遍，否则新下的那条挑不到。
            if (dialog.Touched) changed?.Invoke();
        });

    /// <summary>
    /// 刮削元数据信息：按名字重新问一遍刮削源，抓回来的<b>盖掉</b>现在的。先问一句 —— 手改过的片名、简介、
    /// 分级会被覆盖，而那不是一句提示能挽回的。
    /// <para>
    /// 图片不覆盖（见 <see cref="EmbyClient.RefreshItemAsync"/>），所以刚在「修改媒体封面图」里挑的那张留得住。
    /// </para>
    /// </summary>
    private static Task ScrapeAsync(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card) =>
        GuardAsync(shell, "刮削失败", async () =>
        {
            var item = card.Item;

            var agreed = await ConfirmDialog.For(owner, whenNoRoot: false)(
                    "刮削元数据信息",
                    $"将重新从刮削源抓取「{item.Name}」的元数据，抓到的会覆盖现在的片名、简介和分级等信息"
                        + "（封面等图片不会被替换）。服务器在后台做这件事，可能要过一会儿才看得到结果。",
                    "开始刮削")
                .ConfigureAwait(true);

            if (!agreed) return;

            await TellAsync(session, (client, token) => client.RefreshItemAsync(item.Id, replace: true, token))
                .ConfigureAwait(true);

            shell.Notify($"已请求刮削「{item.Name}」，服务器在后台处理");
        });

    /// <summary>
    /// 刷新元数据信息：缺什么补什么，现有的一个字不动 —— 所以不问，直接发。服务器在后台做，这里只报「已请求」，
    /// 说「已刷新」是替服务器说话。
    /// </summary>
    private static void Refresh(EmbySession session, IShellActions shell, CardItem card) =>
        _ = GuardAsync(shell, "刷新元数据失败", async () =>
        {
            await TellAsync(session, (client, token) => client.RefreshItemAsync(card.Item.Id, replace: false, token))
                .ConfigureAwait(true);

            shell.Notify($"已请求刷新「{card.Item.Name}」的元数据，服务器在后台处理");
        });

    /// <summary>重新扫描媒体库：整台服务器扫一遍，也就是服务器控制台上那颗按钮。管理员账号才用得了。</summary>
    private static void ScanLibrary(EmbySession session, IShellActions shell) =>
        _ = GuardAsync(shell, "扫描媒体库失败", async () =>
        {
            await TellAsync(session, (client, token) => client.ScanLibraryAsync(token)).ConfigureAwait(true);
            shell.Notify("已开始扫描媒体库，服务器在后台处理");
        });

    /// <summary>
    /// 从继续观看中移除。进度留着 —— 下次打开这个条目照旧从上次的地方接着播，它只是不再列在那一排里。
    /// <para>
    /// 做完必须重读这一页：这张卡片要从屏上消失，而「消失」是打不出补丁的。
    /// </para>
    /// </summary>
    private static void HideFromResume(
        EmbySession session,
        IShellActions shell,
        CardItem card,
        Action? changed) =>
        Run(
            session,
            shell,
            "从继续观看中移除失败",
            (client, token) => client.HideFromResumeAsync(card.Item.Id, hide: true, token),
            _ =>
            {
                shell.Notify($"已把「{card.Item.Name}」从继续观看中移除");
                changed?.Invoke();
            });

    /// <summary>
    /// 删除。<b>连磁盘上的文件一起删，删完没得恢复</b>，所以先问一句，而且那句话里要写清删的是什么、删到哪一步。
    /// <para>
    /// 确认框那颗默认键是「取消」（见 <see cref="ConfirmDialog"/>），所以一次误敲回车不会删掉一部电影。
    /// </para>
    /// </summary>
    private static Task DeleteAsync(
        EmbySession session,
        IShellActions shell,
        FrameworkElement owner,
        CardItem card,
        Action? changed) =>
        GuardAsync(shell, "删除失败", async () =>
        {
            var item = card.Item;
            var what = item.DisplayTypeName is { Length: > 0 } kind ? kind : "条目";

            var agreed = await ConfirmDialog.For(owner, whenNoRoot: false)(
                    $"删除这个{what}",
                    $"将从媒体库中删除「{item.Name}」，并把它在服务器磁盘上的文件一起删掉。此操作无法撤销。"
                        + (ItemMenu.IsEpisodeSet(item) ? "里面的所有单集都会被删除。" : ""),
                    "删除")
                .ConfigureAwait(true);

            if (!agreed) return;

            await TellAsync(session, (client, token) => client.DeleteItemAsync(item.Id, token)).ConfigureAwait(true);

            shell.Notify($"已删除「{item.Name}」");
            changed?.Invoke();
        });
}
