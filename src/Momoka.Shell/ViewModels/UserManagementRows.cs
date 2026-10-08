using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Momoka.Emby;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Momoka.Shell.ViewModels;

public sealed partial class ManagedUserRow(EmbyManagedUser user) : ObservableObject
{
    public EmbyManagedUser User { get; } = user;
    public string Id => User.Id;
    public string Name => User.Name;
    public string Role => User.IsAdministrator ? "管理员" : "用户";
    public string Status => User.IsDisabled ? "已禁用" : User.HasPassword ? "已设置密码" : "未设置密码";
    public string LastActive => DateTimeOffset.TryParse(EmbyManagedUser.Text(User.Document, "LastActivityDate"), out var date)
        ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "尚未活动";
    [ObservableProperty] public partial BitmapImage? Avatar { get; set; }
}

public sealed partial class UserOptionRow(string id, string name, bool selected = false, string note = "", bool enabled = true, string alternateId = "") : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Note { get; } = note;
    public bool Enabled { get; } = enabled;
    public string AlternateId { get; } = alternateId;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    public ObservableCollection<UserOptionRow> Children { get; } = [];
    [ObservableProperty] public partial bool Selected { get; set; } = selected;
}

public sealed record UserPermissionGroup(string Name, IReadOnlyList<UserOptionRow> Options);
public sealed record UserValueChoice(string Id, string Name);
public sealed record UserScheduleRow(JsonObject Value)
{
    public string Label => Day(EmbyManagedUser.Text(Value, "DayOfWeek")) + "　" + Clock("StartHour") + " – " + Clock("EndHour");
    private string Clock(string key) => double.TryParse(Value[key]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var hour)
        ? $"{(int)hour:00}:{(int)Math.Round((hour % 1) * 60):00}" : "未知";
    internal static string Day(string value) => value switch
    {
        "Sunday" => "星期日",
        "Monday" => "星期一",
        "Tuesday" => "星期二",
        "Wednesday" => "星期三",
        "Thursday" => "星期四",
        "Friday" => "星期五",
        "Saturday" => "星期六",
        _ => value
    };
}
