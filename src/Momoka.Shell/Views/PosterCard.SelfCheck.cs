using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Momoka.Shell.Views;

public sealed partial class PosterCard
{
    private static readonly string[] HostStates = ["Normal", "PointerOver", "Pressed"];

    private bool HostSurfaceIsTransparent()
    {
        if (Up<Button>(this) is not { } host || HostPresenter(host) is not { } presenter)
            return false;

        var group = VisualStateManager.GetVisualStateGroups(presenter)
            .FirstOrDefault(value => value.Name == "CommonStates");
        if (group is null) return false;
        var previous = group.CurrentState?.Name ?? "Normal";

        try
        {
            foreach (var state in HostStates)
            {
                if (!VisualStateManager.GoToState(host, state, false)) return false;
                host.UpdateLayout();
                if (group.CurrentState?.Name != state || !Transparent(presenter.Background)) return false;
            }

            return Transparent(host.Background);
        }
        finally
        {
            VisualStateManager.GoToState(host, previous, false);
        }

        static bool Transparent(Brush? brush) => brush is null or SolidColorBrush { Color.A: 0 };
    }

    // This exercises the host template without invoking the card or moving the shared desktop pointer.
    internal async Task CaptureHostStatesAsync(Func<string, Task> capture)
    {
        if (Up<Button>(this) is not { } host || HostPresenter(host) is not { } presenter)
            throw new InvalidOperationException("卡片外层按钮模板尚未实现");

        var group = VisualStateManager.GetVisualStateGroups(presenter)
            .FirstOrDefault(value => value.Name == "CommonStates")
            ?? throw new InvalidOperationException("卡片外层按钮缺少 CommonStates");
        var previous = group.CurrentState?.Name ?? "Normal";

        try
        {
            foreach (var state in HostStates)
            {
                if (!VisualStateManager.GoToState(host, state, false) || group.CurrentState?.Name != state)
                    throw new InvalidOperationException($"卡片外层按钮无法进入 {state}");
                host.UpdateLayout();
                await capture(state);
                if (group.CurrentState?.Name != state)
                    throw new InvalidOperationException($"卡片截图期间 {state} 状态被改变");
            }
        }
        finally
        {
            VisualStateManager.GoToState(host, previous, false);
        }
    }

    private static ContentPresenter? HostPresenter(Button host)
    {
        host.ApplyTemplate();
        return VisualTreeHelper.GetChildrenCount(host) > 0
            ? VisualTreeHelper.GetChild(host, 0) as ContentPresenter
            : null;
    }
}
