using EmbyNian.Configuration;
using EmbyNian.Emby;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 「把媒体库的列表也添加到主页之中，新增页里拖拽决定这些列表的顺序，勾选显示或者不勾选取消显示」的那一半规则：
/// <see cref="HomeLayout"/> 把「设置文件里存的那份版面」和「服务器现在有哪几个媒体库」合成这一次要排的几排。
/// <para>
/// 值得单测的正是那三种对不上：新加了一个库、删掉了一个、改了名。屏幕上它们各自看起来都像「主页少了一排」或者
/// 「多了一排空货架」，而这几条断言说得出到底该是什么。
/// </para>
/// </summary>
internal static class HomeLayoutTests
{
    public static void Register()
    {
        Test("主页版面：没有存档时按默认次序，媒体库排在后面", () =>
        {
            var plan = HomeLayout.Plan(null, [Library("1", "电影"), Library("2", "电视节目")]);

            Assert.Equal(6, plan.Count);
            Assert.Equal("继续观看", plan[0].Title);
            Assert.Equal("媒体库", plan[1].Title);
            Assert.Equal("接下来看", plan[2].Title);
            Assert.Equal("最近添加", plan[3].Title);
            Assert.Equal("最近添加 · 电影", plan[4].Title);
            Assert.Equal("最近添加 · 电视节目", plan[5].Title);

            // 默认全部显示：新加的库不该悄悄地不出现。
            Assert.True(plan.All(row => row.Visible), "默认应当全部显示");
        });

        Test("主页版面：存档里那份次序说了算", () =>
        {
            // 用户把最近添加拖到最前面，把媒体库那一排勾掉了。
            List<HomeRowSetting> saved =
            [
                Row(HomeLayout.Latest, "最近添加"),
                Row(HomeLayout.Libraries, "媒体库", visible: false),
                Row(HomeLayout.Resume, "继续观看"),
                Row(HomeLayout.NextUp, "接下来看")
            ];

            var plan = HomeLayout.Plan(saved, [Library("1", "电影")]);

            Assert.Equal("最近添加", plan[0].Title);
            Assert.Equal("媒体库", plan[1].Title);
            Assert.False(plan[1].Visible, "勾掉的那一排要留着不显示，而不是消失");
            Assert.Equal("继续观看", plan[2].Title);
            Assert.Equal("接下来看", plan[3].Title);

            // 存档里没有的补在末尾。
            Assert.Equal(5, plan.Count);
            Assert.Equal("最近添加 · 电影", plan[4].Title);
            Assert.True(plan[4].Visible);
        });

        Test("主页版面：服务器上新加的库排到末尾，删掉的那个扔掉", () =>
        {
            List<HomeRowSetting> saved =
            [
                Row(HomeLayout.Resume, "继续观看"),
                Row(HomeLayout.LibraryKey("gone"), "最近添加 · 纪录片", visible: false),
                Row(HomeLayout.Libraries, "媒体库"),
                Row(HomeLayout.NextUp, "接下来看"),
                Row(HomeLayout.Latest, "最近添加")
            ];

            var plan = HomeLayout.Plan(saved, [Library("new", "电影")]);

            // 那个库已经不在服务器上了：这一排整个扔掉 —— 留着就是一排永远空的货架。
            Assert.Equal(5, plan.Count);
            Assert.True(plan.All(row => HomeLayout.LibraryId(row.Key) != "gone"), "删掉的库还在版面里");

            Assert.Equal("最近添加 · 电影", plan[^1].Title);
            Assert.Equal(HomeLayout.LibraryKey("new"), plan[^1].Key);
        });

        Test("主页版面：库列表不在手上时用存档里记着的名字", () =>
        {
            // 设置窗口是这种情形：它有设置文件，没有服务器。主窗口读库列表失败时也是空手。
            List<HomeRowSetting> saved =
            [
                Row(HomeLayout.LibraryKey("4"), "最近添加 · 电视节目", visible: false),
                Row(HomeLayout.Resume, "继续观看"),
                Row(HomeLayout.Libraries, "媒体库"),
                Row(HomeLayout.NextUp, "接下来看"),
                Row(HomeLayout.Latest, "最近添加")
            ];

            // 「没有那份列表」和「那份列表是空的」都算空手。
            IReadOnlyList<EmbyItem>?[] empty = [null, []];

            foreach (var unknown in empty)
            {
                var plan = HomeLayout.Plan(saved, unknown);

                Assert.Equal(5, plan.Count);
                Assert.Equal("最近添加 · 电视节目", plan[0].Title);
                Assert.False(plan[0].Visible, "勾没了");

                // 这一份跟存档一模一样，所以主页那一头不会把它当「变了」写回去 —— 那正是抹掉那几排的路。
                Assert.True(HomeLayout.Same(saved, plan), "空手时不该改动存档里那一份");
            }

            // 存档里连名字都没记：这一排写不出标题，只能扔。
            var nameless = HomeLayout.Plan([Row(HomeLayout.LibraryKey("4"), "")], null);
            Assert.Equal(4, nameless.Count);
            Assert.True(nameless.All(row => HomeLayout.LibraryId(row.Key) is null), "没有名字的库排进了版面");
        });

        Test("主页版面：库改了名就用新名字", () =>
        {
            List<HomeRowSetting> saved = [Row(HomeLayout.LibraryKey("1"), "最近添加 · 老名字", visible: false)];

            var plan = HomeLayout.Plan(saved, [Library("1", "新名字")]);

            Assert.Equal("最近添加 · 新名字", plan[0].Title);

            // 名字换了，勾没换：那个勾是用户自己按的。
            Assert.False(plan[0].Visible);
        });

        Test("主页版面：认不出来的钥匙和没有 id 的库都不进版面", () =>
        {
            List<HomeRowSetting> saved = [Row("nonsense", "手改坏了的一行"), Row("", "空钥匙")];

            var noId = new EmbyItem { Id = "", Name = "没有 id 的库" };
            var plan = HomeLayout.Plan(saved, [noId, Library("1", "电影")]);

            Assert.Equal(5, plan.Count);
            Assert.True(plan.All(row => row.Key != "nonsense" && row.Key.Length > 0), "认不出的钥匙进了版面");
            Assert.Equal(HomeLayout.LibraryKey("1"), plan[^1].Key);
        });

        Test("主页版面：钥匙认得出媒体库那几排", () =>
        {
            Assert.Equal("library:abc", HomeLayout.LibraryKey("abc"));
            Assert.Equal("abc", HomeLayout.LibraryId("library:abc"));
            Assert.Equal(null, HomeLayout.LibraryId(HomeLayout.Latest));
            Assert.Equal(null, HomeLayout.LibraryId(null));

            // 四排固定的各有一句话；媒体库那几排的标题不从这里来。
            Assert.Equal("继续观看", HomeLayout.FixedTitle(HomeLayout.Resume));
            Assert.Equal("最近添加", HomeLayout.FixedTitle(HomeLayout.Latest));
            Assert.Equal(null, HomeLayout.FixedTitle("library:1"));
        });

        Test("主页版面：只有真变了才回写设置文件", () =>
        {
            var libraries = new[] { Library("1", "电影") };
            var plan = HomeLayout.Plan(null, libraries);
            var saved = HomeLayout.Save(plan);

            Assert.True(HomeLayout.Same(saved, plan), "刚存下来的那一份应当算没变");
            Assert.True(HomeLayout.Same(saved, HomeLayout.Plan(saved, libraries)), "再算一遍也该是同一份");

            // 少一排、次序不同、勾不同，三种都算变了。
            Assert.False(HomeLayout.Same(saved.Take(2).ToList(), plan));
            Assert.False(HomeLayout.Same(null, plan));

            var reordered = HomeLayout.Save(plan.Reverse());
            Assert.False(HomeLayout.Same(reordered, plan));

            var unticked = HomeLayout.Save(plan);
            unticked[0].Visible = false;
            Assert.False(HomeLayout.Same(unticked, plan));
        });
    }

    private static EmbyItem Library(string id, string name) =>
        new() { Id = id, Name = name, Type = EmbyItemType.CollectionFolder };

    private static HomeRowSetting Row(string key, string title, bool visible = true) =>
        new() { Key = key, Title = title, Visible = visible };
}
