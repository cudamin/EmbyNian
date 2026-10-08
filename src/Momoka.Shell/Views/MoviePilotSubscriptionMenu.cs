using Momoka.MoviePilot;
using Momoka.Shell.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Momoka.Shell.Views;

internal static class MoviePilotSubscriptionMenu
{
    internal static MenuFlyout Create(FrameworkElement owner, MoviePilotSubscriptionsViewModel vm,
        MoviePilotSubscriptionCard card, Action open)
    {
        var menu = new MenuFlyout();
        var needsReview = vm.NeedsReview(card);
        var pauseAction = card.Subscription.Paused ? MoviePilotSubscriptionAction.Resume : MoviePilotSubscriptionAction.Pause;
        if (needsReview) Add("核对后继续", "\uE946", () => _ = vm.ReviewAsync(card), false);
        Add("编辑", "\uE70F", () => _ = vm.EditAsync(card, subscription => MoviePilotSubscriptionEditor.ShowAsync(owner, subscription)));
        Add("搜索", "\uE721", () => _ = vm.ManageAsync(card, MoviePilotSubscriptionAction.Search));
        Add(card.Subscription.Paused ? "恢复" : "暂停", card.Subscription.Paused ? "\uE768" : "\uE769",
            () => _ = vm.ManageAsync(card, pauseAction));
        Add("重置", "\uE777", () => _ = vm.ManageAsync(card, MoviePilotSubscriptionAction.Reset));
        Add("媒体详情", "\uE946", open, false);
        Add("文件统计", "\uE9F9", open, false);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("取消订阅", "\uE74D", () => _ = vm.ManageAsync(card, MoviePilotSubscriptionAction.Delete));
        return menu;

        void Add(string title, string glyph, Action action, bool write = true)
        {
            var item = new MenuFlyoutItem { Text = title, Icon = new FontIcon { Glyph = glyph }, IsEnabled = vm.Owns(card) && (!write || vm.CanManage && !needsReview) };
            AutomationProperties.SetAutomationId(item, "Subscription" + title);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }
}
