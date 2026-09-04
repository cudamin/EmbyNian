# libmpv 的运行时依赖

这里只放一个文件：`vulkan-1.dll`。它是 `libmpv-2.dll` **唯一一个不属于 Windows 系统的依赖**，
由 `src/EmbyNian.Shell/EmbyNian.Shell.csproj` 当普通内容文件拷到 exe 旁边，普通构建和发布都拷。

2026-09-04 入库。**从前它不在仓库里** —— 发布时从 `C:\mpv_config-2026.08.12`（用户自己那套便携版 mpv）
的根目录现拷，也就是说这个程序能不能发布、发出来能不能起播，取决于另一个软件的安装目录还在不在。
着色器文件 2026-09-03 因为同一个理由入库（见 `assets/shaders/README.md`），这是同一件事的最后一块。

| 项 | 值 |
|---|---|
| 出处 | `C:\mpv_config-2026.08.12\vulkan-1.dll`，一字节不差 |
| 版本 | 1.4.359.0（`1.4.359.Dev Build`，Vulkan Runtime 的 loader） |
| 大小 | 581,120 字节 |
| SHA256 | `13dd448a97495763b5cce84b8ade84cd2fa13c71f7e871f3c7d509eca845ad0f` |
| 许可 | Apache-2.0（Khronos Vulkan-Loader） |

## 为什么它必须跟着发

两条，各自都足够：

- **它是 `libmpv-2.dll` 的静态导入**（这份 dll 里一张延迟加载表都没有），所以找不到它的机器上
  libmpv 连加载都失败，一帧都放不出来 —— 不是「切到 vulkan 才用得上」。
- **图形接口的出厂默认就是 `vulkan`**（v8 起，见 `MpvRenderCheck.PreferredApi`）。

**不能指望系统里那一份**：`C:\Windows\System32\vulkan-1.dll` 是显卡驱动装的，没有 Vulkan 驱动的机器上
就没有这个文件；而且它常常更旧 —— 这台机器上系统那份是 1.3.250，箱子里这份是 1.4.359。

## 为什么没有 `lua51.dll`

从前发布时是把那个 mpv 目录根下**所有** dll 拷过来，也就是 `lua51.dll` 和 `vulkan-1.dll` 两个。
前者是白拷的 734 KB：**`libmpv-2.dll` 根本不加载它** —— 导入表里没有，延迟加载表不存在，
整个二进制里连 `lua51.dll` 这个字符串都不出现（LuaJIT 2.1 是静态编译进去的，
`--version` 的功能表里那个 `luajit` 指的就是它）。所以 2026-09-04 起不再装箱。

## 升级 `libmpv-2.dll` 之后要做的一件事

**重新读一遍它的导入表**，看有没有出现新的、不属于系统的 dll；有就照上面的样子一起放进这个目录。
办法是直接解析 PE 的导入目录（DOS 头 `e_lfanew` → PE 头 → 数据目录第 1 项是导入、第 13 项是延迟加载，
RVA 用节表换成文件偏移，逐个描述符读 dll 名）。当前这份（v0.41.0-923）读出来是 47 个静态导入，
除了 `vulkan-1.dll` 全是 Windows 自带的（`KERNEL32`、`USER32`、`d2d1`、`DWrite`、`OPENGL32`、
`api-ms-win-*` 那一批），延迟加载表为空。
