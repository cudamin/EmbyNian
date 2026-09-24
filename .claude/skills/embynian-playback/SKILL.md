---
name: "embynian-playback"
description: "EmbyNian playback: libmpv and external-mpv backends, integrated and standalone pipelines, playback planning and progress, resume, OSD, subtitles, audio tracks and source switching. Use when touching Core/Playback, Core/Mpv, PlayerViewModel, PlayerPage, or assets/mpv-ui Lua/uosc controls, including standalone menus and gestures; authorization and verification policy live in CLAUDE.md."
---

# EmbyNian — playback

EmbyNian is a WinUI 3 + .NET desktop client, unpackaged and x64, with libmpv as its built-in playback core. Work from the current checkout. Earlier application names survive in settings migration; current names and paths come from the source.

**Policy lives in [CLAUDE.md](../../../CLAUDE.md)**: playback authorization and probe coverage, credentials, navigation, dependency upgrades and verification gates. Read that policy rather than maintaining another copy here. `PROGRESS.md` records in-flight work and earlier measurements.

Architecture is `embynian-winui-shell`; evidence and measurements are `embynian-verification`; shaders are `mpv-shader-quality`.

Operational details:

- The normal player's self-check uses synthetic state without loading a film. Local-file probes are a different path; their isolation and integrated-only coverage are specified in CLAUDE.md. Neither proves standalone playback works.
- **`libmpv-2.dll` lives in the current working-tree root**, is linked into build output by `EmbyNian.Shell.csproj` and copied next to the exe on publish. The binary is not tracked; a new worktree needs an explicitly supplied, matching copy before publishing.
- **Keep credentials out of URLs, process arguments and logs:** `EmbyUrl.Stream` carries no `api_key`; the token is an `X-Emby-Token` header. libmpv receives options in-process. The external backend starts idle without headers/media/subtitle arguments, verifies the IPC server belongs to the process it just launched, then applies headers before loading any media. Do not print header option values or raw IPC/stderr payloads; log redaction alone cannot protect process arguments.
- External `EnableIpc=false` disables playback observation/control/progress, not the authenticated startup/quit transport. Failure to establish that startup transport must stop startup rather than fall back to credentials in arguments or temporary files. This channel does not claim to defend against malicious code already running as the same user.

## The shape of playback

`IPlaybackBackend` is the seam, and it is what lets both backends exist at once: everything above it — the planner, the progress reports, the player chrome — only ever sees a `PlaybackRequest` going in and an `IPlaybackHandle` coming out, so the choice stays a per-play setting rather than an architectural commitment.

- **`LibMpvBackend`** — libmpv loaded in-process, with integrated composition rendering or a standalone native window according to the pipeline. `LibMpvBackend.Locate(MpvSettings)` is the shared library-location probe.
- **`MpvProcessBackend`** — the user's own `mpv.exe` in a window of its own, driven over IPC.
- **`PlaybackBackendFactory`** picks one; the container hands `PlaybackService` a `Func<IPlaybackBackend>` rather than an instance.

**Write cost-sensitive code for the IPC backend, not the in-process one.** Reading a property from in-process libmpv is nearly free and returns synchronously (so twenty-one property reads per second are one unbroken stretch of work on the UI thread); every read on the piped `mpv.exe` is a round trip and wants them stacked. Opposite optima, so the handle carries a capability bit for whether reads may be batched — the decision belongs to the handle, not the caller.

`IPlaybackHandle.HasControlChannel` is false when no control channel came up — position and pause state are then genuinely unknown, and callers must cope rather than read it as 0. **Beware the near-miss**: "is this a control-type handle" is a different question, because the external `mpv.exe` handle is always control-type and whether the channel actually opened is what `HasControlChannel` answers. Getting those two confused once made the client report a full default snapshot (volume 100, not muted) as if it were a reading.

**`PlaybackPlanner.Plan(ticket, connection)` assembles the whole decision** — URL, headers, track ids, shader group, resume position, screenshot directory — and is deliberately kept free of process and socket handling **so the entire decision can be asserted in a unit test**. Keep it that way: new decisions go into the planner or a pure function it calls, not into a backend.

**`PlaybackService` runs one playback start to finish and keeps the server informed while it does.** The server-facing half lives here rather than in the UI on purpose: v1 let the form that happened to start playback own the progress timer, so closing that window stopped the reporting and Emby kept the item marked as playing forever.

## Two rules that break on episode switches

- **`_generation` is the boundary.** It increments on every change of film; every async callback compares it on the way back in and drops itself if it no longer matches. A late result from the previous episode landing in the new one is the easiest mistake to make in this area.
- **Cancellation is not an error.** Stopping, closing and switching episodes all cancel in-flight requests; none of them may surface as an error notice.

## Progress, resume, watched state

- Reports go to `Sessions/Playing`, `Sessions/Playing/Progress` and `Sessions/Playing/Stopped` through `EmbyClient`. **One body shape serves all three** (`Emby/PlaybackReport.cs`) because Emby ignores fields it does not know. `EventName` is `timeupdate` / `pause` / `unpause`; `Failed` marks a stop where the user quit mpv instead of reaching the end. `PlayMethod` is always `DirectStream` — this client hands the original file to mpv and never asks the server to transcode.
- **`VolumeLevel` and `IsMuted` are filled only when `HasControlChannel`**; `null` means "can't say", and that is the point — a report that always says 100 is worse than one that says nothing, because Emby's remote UI displays it and lets someone adjust from it. `Build` is an instance method for this reason.
- Cadence: a `PeriodicTimer` at `settings.Playback.ProgressReportIntervalSeconds`, clamped to 1–60 seconds.
- Resume comes from the item's own user data: `EmbyItem.ResumeTicks` is `UserData.PlaybackPositionTicks`, and `HasResumePosition` additionally requires the fraction to sit strictly between 0.001 and 0.995 — so a barely-started and a nearly-finished item both correctly read as "nothing to resume". `PlayerViewModel` starts at `choice?.StartTicks ?? resumeTicks`.
- Watched / favourite / hide-from-resume go through `EmbyClient` (`Users/{id}/PlayedItems/{id}`, `Users/{id}/FavoriteItems/{id}`, `HideFromResume`) and are driven from `Views/ItemCommands.cs` and its `ItemCommands.Server.cs` half. **`MarkUnplayed` also clears the resume position; 「从继续观看中移除」 deliberately does not** — that distinction is documented at the client method and is worth preserving. Optimistic UI here has bitten once: the 「标记为已观看」 tick jumped back to its old value before the server answered.

## Tracks, episodes, chapters — the pure functions

- **`TrackSelection.Resolve(settings, source)`** returns `AutoTracks(audio, subtitle)`. **Audio language is a priority list, not a mode**: `PlaybackSettings.AudioLanguages` (schema v10, 2026-09-04), and **an empty list is what "follow the server's default audio track" means**. The old `AudioTrackMode` enum was deleted precisely because its two values duplicated "is the list empty" — which is how a language could sit in the settings file and be ignored. `SubtitleMode` is still a mode: Always / ForcedOnly / ForeignAudioOnly / Off.
- `SubtitleChoice` separates 「本片没有合适的字幕，交给 mpv」 (leaves mpv's own `slang` matching in charge) from 「明确不要字幕」 (passes `--sid=no`). Collapsing those two into one nullable is a bug, not a simplification.
- **`TrackLanguagePriority`** maps the names the settings page shows (简体中文, 粤语 …) onto the ISO codes mpv's `alang` / `slang` expect, in priority order, and matches on a track's *title* when its language field says nothing useful (「简体&日语」 on a stream whose Language is only `chi`). Unknown names pass through verbatim, so a user who knows mpv can type `jpn`. The Family + Generic fields are what keep 简体中文 from selecting a 繁体 track. `ParseList` is shared by the audio row and the subtitle row, so both are written the same way.
- **`EpisodeNavigation.Step(episodes, currentItemId, offset)`** resolves 上一集 / 下一集; `offset` must be −1 or 1. **The server owns the ordering** — this rule only walks that order, then narrows the result back to one season so the player's 选集 menu stays seasonal.
- **Chapters exist twice and both are right.** Emby's scan (`EmbyItem.Chapters`) and mpv's own `chapter-list` disagree on remuxed files, and **what is on screen (mpv) wins**. But chapter thumbnails are fetched by **Emby's** index (`/Items/{id}/Images/Chapter/{index}`), so images follow Emby's table while names follow mpv's. Don't mix the two.
- Also in `Core/Playback`, reuse rather than re-derive: `SkipSections` / `SkipCoordinator` (where 片头/片尾 come from, and when the button appears or auto-fires), `ChapterTimeline`, `AspectLock`, `PlaybackStats`, `PlayerPalette`, `PinIndicator`, `CursorMask`, `ChromeReveal` (the OSD's show/hide rules as a UI-free state machine), `PictureTap`, `PulseArt`, `ShaderGroupResolver`, `OutputWatch` / `ShaderSurface`, `AudioDeviceCatalogue`. mpv-side plumbing (argument building, IPC protocol, config documents, track maps, shader catalogue, `LibMpvNodes`) lives in `Core/Mpv`.
- Playback starts with `--no-config` / `config=no`, so the settings page's mpv.conf / input.conf editing is explicitly an external-configuration tool and never overrides the client's own playback arguments.

## 字幕外观 — two paths, and two mpv traps that cost nine rows (2026-09-05)

- **One writer per option, and it is `MpvOutputOptions.SubtitleAppearance`.** It returns the eleven appearance options; `Build` calls it and adds the two that are launch-only, `sub-codepage` (used when the subtitle is decoded) and the image-sub stretch (needs the film's aspect). `sub-font` used to travel separately on `PlaybackRequest.SubtitleFont` — that property is gone, and `FontFamilies.Resolve` is where 「族名还是文件路径」 is decided now.
- **Changing a row reaches the film that is playing**: `PlaybackService.ApplySubtitleStyleAsync`, handed to the settings page as a single method. It walks `MpvOutputOptions.SubtitleStyleOptions` and **asks mpv for the default of anything the settings no longer name** — on a running player 「don't send it」 means 「keep what I sent a minute ago」, so a row switched back to 「不设置」 would otherwise stick. A test asserts the name list covers everything `SubtitleAppearance` can emit.
- **`sub-ass-override` defaults to `scale`, so an ASS/SSA subtitle ignores font, size, bold, colour, outline, shadow and plate.** Only `sub-scale` gets through. That is what emptied nine rows of the 字幕 card for every fansubbed release in the library; 设置 → 字幕 → 外观应用范围 = 强制 sends `force`. A PGS/VOBSUB track is a picture and takes none of them either way.
- **`sub-back-color` paints nothing without `sub-border-style`.** Since mpv 0.39 that colour is shared with the drop shadow, and the style option alone decides which of the two is drawn; unset (mpv's `outline-and-shadow`) there is no plate at any colour or opacity. 字幕底板 is that option, and 底板颜色's note restates which job the colour is currently doing.
- **Ask the shipped libmpv rather than the manual for a default.** `option-info/<name>/default-value` and `/choices` are readable off a bare context (no file, no window), which is how the two traps above were pinned and how three stale 「mpv 自己的默认是 X」 comments were caught in one pass — `sub-font-size` is 38 and not 55, `sub-border-size` is 1.65 and not 3, `sub-codepage` is `auto`. Rendering a frame to see the result needs `vo=null` plus `screenshot-to-file … subtitles`; `vo=image` writes the bare video frame with no subtitle on it.
- **字幕编码 defaults to 自动识别, not `gb18030`** (schema v11 clears a stored `gb18030`). Naming a codepage switches mpv's detection off, so the old default read Big5 繁体 files as 简体 and produced mojibake, while detection handles GBK identically — measured both ways, 2026-09-05.

## Audio and video output (the 2026-09-04 batch)

- **The volume ceiling is `AudioSettings.MaxVolume` = 130**, and 130 is mpv's own `volume-max` default — chosen so the client never has to send `volume-max` and the ceiling has no second source. Six places reference the constant, XAML's slider `Maximum` among them. The trap it left behind: `MpvOutputOptions` sends `volume` only when it is `>= 0 and <= MaxVolume and not 100`, and the old `< 100` test silently sent nothing for a stored 130, so mpv started at 100 while the slider showed 130. The upper bound is not a duplicate clamp — a value above mpv's `volume-max` makes `mpv.exe` exit rather than play.
- **`AudioDeviceCatalogue`** opens a throwaway libmpv handle purely to read `audio-device-list` (`video=no`, `vo=null`, so no window can appear while the settings page is open), then terminates it. Registered as a concrete class, not behind an interface — it has one public method. `AutoDevice` is `"auto"`, mpv's own always-first entry, and it is **filtered out** so the empty-string 「跟随系统默认设备」 row is the only one carrying that meaning; a stored `"auto"` is normalised to empty in migration.
- **`LibMpvNodes`** is the one place that reads mpv's node trees (`track-list` and `audio-device-list` are the same `mpv_node` structure). It encodes three things that are easy to get wrong and invisible when wrong — an int64 lives *in* the union rather than at what it points to, a flag is a 4-byte int whose upper half is leftover heap, a map's keys and values are two parallel arrays — and it owns the free, which is the half that gets forgotten.
- **`PlaybackService.AudioDeviceInUse`** is which device this playback actually got, polled once after playback starts (audio output is not open yet at the moment the file is handed over) from `audio-device` plus `current-ao`. It cannot be read off the launch arguments: those say what the client *asked* for, and 「跟随系统默认设备」 asks for nothing.
- **Screenshots** land in `AppPaths.ScreenshotDirectory` (`%LOCALAPPDATA%\EmbyNian\screenshots`, created at startup). Three options come from `MpvBaseline`: the directory, `screenshot-format=png`, and a template **built in C#** — mpv's `%F` would name the Emby stream URL, and its `%p` timecode contains colons, which are illegal in Windows filenames. The template ends in `-%02n` because **mpv will not overwrite an existing screenshot and only looks for another name when the template is numbered**; without it, two shots of the same paused frame silently produce one file while the UI says both were saved. The title goes through the same `DownloadPlan.Safe` the download feature uses, plus a percent guard mpv would otherwise read as a format specifier.
- **`VideoSettings.FillWideSources`** sends `panscan=1.0`, but only when the source is genuinely letterboxed (`AspectRatio > MpvOutputOptions.WideAspect`, 1.79) — handing a 16:9 file to it just crops for nothing. That 1.79 was hard-coded in the subtitle-stretch line before; the two uses are halves of one fact (the picture does not fill this frame).
- **`OutputWatch` / `ShaderSurface`** answer how large the picture can be drawn right now and how large it would be full-screen, in physical pixels. When the size cannot be read, the caller logs one line and **must not remember the monitor's resolution as a known size**.

## The shell side

`ViewModels/PlayerViewModel.cs` is where playback state converges, and it is **the only view model in the DI container** — a film keeps playing behind the library page, so its state has to outlive navigation. `Views/PlayerPage` is split into `.xaml`, `.xaml.cs`, `.Chrome.cs`, `.Input.cs`, `.Menus.cs`, `.Panels.cs`, `.Fonts.cs`, `.Palette.cs` plus a set of `.SelfCheck.*` files.

**`PlayerPage` is a sibling `UserControl` of the navigation shell, not a page inside the `Frame`.** It hosts the integrated pipeline's composition surface and owns the playback title bar, window controls and dragging. The standalone pipeline uses mpv's native top-level window and uosc instead; do not infer its behavior from the integrated page.

**mpv's `dwidth`/`dheight` exist before the video is configured and fall back to the window's client size then** (libmpv 0.41, measured off the 2026-09-12 上下黑边 incident in PROGRESS.md — a slowly decoding mp4 answered the aspect poll with the window's own shape, and the window then locked to itself, letterboxing the real picture). `video-params/*` is the family that genuinely appears only once the video is known: gate any read of the display pair on `video-params/w` first. The same belief in another guise — 「the properties do not exist until a frame is decoded」 — sat in the aspect poll's own comment for months, so don't trust a property-existence claim that a 404'd or remuxed file never got to exercise.

Two rules about the 10 Hz ticker:

- **It must not stop.** It drives `ViewModel.Tick()` (coalesced `percent-pos` seeks, stats refresh, volume push, the 「跳过」 button floating up) *and* pointer polling — which is how the player notices the hand moved after the cursor was hidden. `ChromeReveal.Pending` going false only means the OSD finished collapsing, not that the timer may stop. To save cost, make the idle tick cheap; don't stop the clock.
- **Don't `new` anything per tick.** Rebuilding a `TextBlock`, `FontFamily` or `Border` every second accumulates into visible GC pressure over a feature-length film; reuse the elements that are already there.

## Tests

`tests/EmbyNian.Tests` currently references Core. Important playback rules belong in independently testable code; use the layering policy in CLAUDE.md rather than moving every local UI predicate into Core. Relevant suites include `PlaybackTests.cs`, `InlineSwitchTests.cs`, `MpvUiTests.cs`, `PinIndicatorTests.cs`, `PlayerPaletteTests.cs`, `ItemDetailTests.cs`, `StartupArgsTests.cs` and `SettingsTests.cs`. Reuse the appropriate suite and register new suites in `Program.cs`.

When changing playback synchronisation, check stop/close, episode switch, auto-next, resume, progress reporting, server-state write-back and cancellation as applicable. Pure rules can be unit-tested; static Lua/source assertions do not prove runtime behavior. Use isolated probes only for the paths they actually exercise, and do not test server write-back without the authorization in CLAUDE.md.

Run the applicable gates from CLAUDE.md; consult `embynian-verification` for interpreting results. Report any affected standalone behavior that lacks runtime verification.
