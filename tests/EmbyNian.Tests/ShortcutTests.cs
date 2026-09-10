using EmbyNian.Playback;
using static EmbyNian.Tests.TestHarness;

namespace EmbyNian.Tests;

/// <summary>
/// 播放器快捷键那张表的判断，全在 <see cref="ShortcutCatalog"/>（Core 纯函数）。这些是它的钉子 —— 单元测试进不到
/// 外壳那个程序集，而「这个键触发哪个动作」「绑到已占用的键上会不会拦下」「存文件长什么样、显示成什么样」都是
/// 屏上看不出对错、编译也看不出的判断。
/// </summary>
internal static class ShortcutTests
{
    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private static KeyStroke Key(string key, bool ctrl = false, bool alt = false, bool shift = false) =>
        new(key, ctrl, alt, shift);

    public static void Register()
    {
        Test("快捷键表：19 个动作，Id 不重复、默认键不重复、没有一个默认落在保留键上", () =>
        {
            Assert.Equal(19, ShortcutCatalog.Actions.Count);

            var ids = ShortcutCatalog.Actions.Select(a => a.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count(), "动作 Id 有重复");

            Assert.True(ShortcutCatalog.DefaultsAreUnique(), "装机就带着两个动作共用一个默认键 —— Rebind 拦不住这种，只有测试拦得住");

            foreach (var action in ShortcutCatalog.Actions)
                Assert.False(ShortcutCatalog.IsReserved(action.Default), $"{action.Id} 的默认键是保留键");
        });

        Test("快捷键表：默认键就是改造前那张 switch 表 —— 空格暂停、F 全屏、Shift+Z 字幕延迟增大", () =>
        {
            KeyStroke Default(string id) => ShortcutCatalog.Actions.First(a => a.Id == id).Default;

            Assert.Equal(Key("Space"), Default("toggle-pause"));
            Assert.Equal(Key("F"), Default("toggle-fullscreen"));
            Assert.Equal(Key("BracketLeft"), Default("speed-down"));
            Assert.Equal(Key("Z"), Default("subtitle-delay-decrease"));
            Assert.Equal(Key("Z", shift: true), Default("subtitle-delay-increase"));
        });

        Test("Resolve：没动过用默认，空串是解绑，动过用动过的", () =>
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["toggle-mute"] = "K",      // 动过
                ["toggle-pin"] = ""         // 显式解绑
            };

            var effective = ShortcutCatalog.Resolve(bindings);

            Assert.Equal(Key("Space"), effective["toggle-pause"], "没动过的应当落到默认");
            Assert.Equal(Key("K"), effective["toggle-mute"], "动过的应当用动过的");
            Assert.True(effective["toggle-pin"].IsEmpty, "空串应当是解绑");
        });

        Test("Resolve：认不出的写法退回默认，而不是让这个动作没有键", () =>
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal) { ["toggle-pause"] = "A+B" };
            var effective = ShortcutCatalog.Resolve(bindings);
            Assert.Equal(Key("Space"), effective["toggle-pause"]);
        });

        Test("Lookup：默认键找得到动作、解绑和空键都找不到、改过的旧键落空", () =>
        {
            Assert.Equal("toggle-pause", ShortcutCatalog.Lookup(Empty(), Key("Space")));
            Assert.Equal("toggle-fullscreen", ShortcutCatalog.Lookup(Empty(), Key("F")));
            Assert.Equal("subtitle-delay-increase", ShortcutCatalog.Lookup(Empty(), Key("Z", shift: true)));
            Assert.Null(ShortcutCatalog.Lookup(Empty(), KeyStroke.None), "空键谁都不触发");

            // 把静音从 M 改到 K：K 找得到静音，M 从此谁都不是。
            var moved = new Dictionary<string, string>(StringComparer.Ordinal) { ["toggle-mute"] = "K" };
            Assert.Equal("toggle-mute", ShortcutCatalog.Lookup(moved, Key("K")));
            Assert.Null(ShortcutCatalog.Lookup(moved, Key("M")), "旧键改走之后就不该再触发");
        });

        Test("Rebind：绑一个空键，存进字典、查得到", () =>
        {
            var result = ShortcutCatalog.Rebind(Empty(), "toggle-mute", Key("K"));

            Assert.Null(result.Conflict);
            Assert.Equal("K", result.Bindings["toggle-mute"]);
            Assert.Equal("toggle-mute", ShortcutCatalog.Lookup(result.Bindings, Key("K")));
        });

        Test("Rebind：冲突就拦下 —— 返回占用者、一个绑定都不改", () =>
        {
            var bindings = Empty();
            var result = ShortcutCatalog.Rebind(bindings, "toggle-mute", Key("F")); // F 是全屏的默认键

            Assert.Equal("toggle-fullscreen", result.Conflict);
            Assert.Equal(0, result.Bindings.Count, "冲突时绑定不该有任何改动");
            // 原字典没被改（返回的就是传进去那一份）
            Assert.Equal(0, bindings.Count);
        });

        Test("Rebind：绑回自己的默认键，就把这条改动删掉（回到「没动过」）", () =>
        {
            var moved = new Dictionary<string, string>(StringComparer.Ordinal) { ["toggle-mute"] = "K" };
            var result = ShortcutCatalog.Rebind(moved, "toggle-mute", Key("M")); // M 就是静音的默认

            Assert.Null(result.Conflict);
            Assert.False(result.Bindings.ContainsKey("toggle-mute"), "绑回默认应当删掉这条改动记录，而不是把默认再写一遍");
            Assert.Equal("toggle-mute", ShortcutCatalog.Lookup(result.Bindings, Key("M")));
        });

        Test("Rebind：保留键（Esc/Y）和空键都当空操作，绑定不动", () =>
        {
            var esc = ShortcutCatalog.Rebind(Empty(), "toggle-pause", Key("Escape"));
            Assert.Null(esc.Conflict);
            Assert.Equal(0, esc.Bindings.Count);

            var y = ShortcutCatalog.Rebind(Empty(), "toggle-pause", Key("Y"));
            Assert.Equal(0, y.Bindings.Count);

            var empty = ShortcutCatalog.Rebind(Empty(), "toggle-pause", KeyStroke.None);
            Assert.Equal(0, empty.Bindings.Count);
        });

        Test("Clear：解绑存空串，Resolve 变成没有键、Lookup 落空", () =>
        {
            var cleared = ShortcutCatalog.Clear(Empty(), "toggle-pause");

            Assert.Equal("", cleared["toggle-pause"], "解绑存的是空串，不是删键 —— 删键会退回默认");
            Assert.True(ShortcutCatalog.Resolve(cleared)["toggle-pause"].IsEmpty);
            Assert.Null(ShortcutCatalog.Lookup(cleared, Key("Space")), "解绑之后原来的默认键就不该再触发");
        });

        Test("Serialize / Parse：来回一趟不走样，只有修饰键或两个键都算解析失败", () =>
        {
            void RoundTrip(KeyStroke stroke)
            {
                Assert.True(ShortcutCatalog.TryParse(ShortcutCatalog.Serialize(stroke), out var back));
                Assert.Equal(stroke, back);
            }

            RoundTrip(Key("Space"));
            RoundTrip(Key("Z", shift: true));
            RoundTrip(Key("S", ctrl: true, alt: true));
            RoundTrip(Key("BracketLeft"));

            Assert.Equal("Shift+Z", ShortcutCatalog.Serialize(Key("Z", shift: true)));
            Assert.Equal("Ctrl+Alt+S", ShortcutCatalog.Serialize(Key("S", ctrl: true, alt: true)));
            Assert.Equal("", ShortcutCatalog.Serialize(KeyStroke.None));

            Assert.False(ShortcutCatalog.TryParse("", out _), "空串不是一次按键");
            Assert.False(ShortcutCatalog.TryParse("Ctrl", out _), "只有修饰键不是一次按键");
            Assert.False(ShortcutCatalog.TryParse("A+B", out _), "两个非修饰键不是一次按键");
        });

        Test("Format：修饰键带空格、特殊键翻成人话、空键是「未设置」", () =>
        {
            Assert.Equal("空格", ShortcutCatalog.Format(Key("Space")));
            Assert.Equal("←", ShortcutCatalog.Format(Key("Left")));
            Assert.Equal("[", ShortcutCatalog.Format(Key("BracketLeft")));
            Assert.Equal("Ctrl + Alt + S", ShortcutCatalog.Format(Key("S", ctrl: true, alt: true)));
            Assert.Equal("Shift + Z", ShortcutCatalog.Format(Key("Z", shift: true)));
            Assert.Equal("未设置", ShortcutCatalog.Format(KeyStroke.None));
        });

        Test("IsReserved：Esc 和 Y 是保留键（连着修饰键也算），别的不是", () =>
        {
            Assert.True(ShortcutCatalog.IsReserved(Key("Escape")));
            Assert.True(ShortcutCatalog.IsReserved(Key("Y")));
            Assert.True(ShortcutCatalog.IsReserved(Key("Y", ctrl: true)), "带修饰键的 Y 也保留 —— 固定的那颗 Y 键不查修饰键");
            Assert.False(ShortcutCatalog.IsReserved(Key("F")));
            Assert.False(ShortcutCatalog.IsReserved(Key("Space")));
        });

        Test("Clean：认不出的 Id、解析不了的、落在保留键上的都清掉，空串留着", () =>
        {
            var dirty = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["不存在的动作"] = "F",           // 认不出的 Id
                ["toggle-pause"] = "Ctrl",        // 只有修饰键，解析不了
                ["seek-forward"] = "Right",       // 好的，留
                ["toggle-mute"] = "",             // 显式解绑，留
                ["volume-up"] = "Escape"          // 落在保留键上
            };

            var clean = ShortcutCatalog.Clean(dirty);

            Assert.False(clean.ContainsKey("不存在的动作"));
            Assert.False(clean.ContainsKey("toggle-pause"));
            Assert.False(clean.ContainsKey("volume-up"));
            Assert.Equal("Right", clean["seek-forward"]);
            Assert.Equal("", clean["toggle-mute"]);
        });

        Test("Clean：null 一份不炸，给回空字典", () =>
        {
            Assert.Equal(0, ShortcutCatalog.Clean(null).Count);
        });

        Test("Label：找得到给中文名，找不到把 Id 原样给回去", () =>
        {
            Assert.Equal("播放 / 暂停", ShortcutCatalog.Label("toggle-pause"));
            Assert.Equal("没这个", ShortcutCatalog.Label("没这个"));
        });
    }
}
