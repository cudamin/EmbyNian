---
name: "embynian-playback"
description: "EmbyNian playback: the two libmpv backends, PlaybackPlanner, progress reporting to Sessions/Playing, resume points, watched/favourite state, audio and subtitle track selection, episode navigation, chapters and skip, the player's OSD, audio output device and screenshots. Use when touching src/EmbyNian.Core/Playback, src/EmbyNian.Core/Mpv, ViewModels/PlayerViewModel.cs or Views/PlayerPage.* in the EmbyNian project (C:\\Users\\89400\\EmbyNian)."
---

# EmbyNian — playback

EmbyNian is an Emby desktop client (WinUI 3 + .NET 10, unpackaged x64, repo at `C:\Users\89400\EmbyNian`) whose playback core is **libmpv**. Earlier names EmbyGearless / EmbyMpvClient survive only in the settings-migration path in `Core/Infrastructure/AppPaths.cs`; anything calling the project by an old name is out of date.

**The rules are in `CLAUDE.md`, which is loaded whenever this skill is, and it outranks this file on every conflict** — never start real playback while verifying, never let the access token into a URL, a log or the self-check report, don't change `libmpv-2.dll`'s version, and navigate with switches rather than the mouse. This file keeps no second copy of them. `PROGRESS.md`'s 在途工作 section is the only cross-window handoff.

Architecture rules (DI, the `Attach` pattern, judgments into Core) are the `embynian-winui-shell` skill; the gate procedure is `embynian-verification`; shaders and 画质档位 are `mpv-shader-quality`.

Three things about those constraints that `CLAUDE.md` doesn't say:

- **The player's own self-check probes run without loading a film at all** — synthetic sections and a collapsed player. If a change genuinely needs a real file, use a local one or hand it to the user; never point it at the server.
- **`libmpv-2.dll` lives in the repo root**, is linked into the build output by `EmbyNian.Shell.csproj` and copied next to the exe on publish; `tools/publish.ps1` throws 「找不到 …libmpv-2.dll」 if it is missing.
- **How the token stays out of everything:** `EmbyUrl.Stream` deliberately carries no `api_key` — the token travels in an `X-Emby-Token` header, which mpv is handed via `--http-header-fields` — and `EmbyHttp` strips the query before anything is logged.

## The shape of playback

`IPlaybackBackend` is the seam, and it is what lets both backends exist at once: everything above it — the planner, the progress reports, the player chrome — only ever sees a `PlaybackRequest` going in and an `IPlaybackHandle` coming out, so the choice stays a per-play setting rather than an architectural commitment.

- **`LibMpvBackend`** — libmpv loaded in-process, rendering into the client's own window. `LibMpvBackend.Locate(MpvSettings)` is the **single answer** for where `libmpv-2.dll` is; that probe used to be private to the backend and is now shared.
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

**`PlayerPage` is a sibling `UserControl` of the navigation shell, not a page inside the `Frame`**: it is a transparent XAML island with mpv's child window showing through from underneath. That is why the title bar, the window buttons and window dragging during playback are this page's own job rather than the shell's.

**mpv's `dwidth`/`dheight` exist before the video is configured and fall back to the window's client size then** (libmpv 0.41, measured off the 2026-09-12 上下黑边 incident in PROGRESS.md — a slowly decoding mp4 answered the aspect poll with the window's own shape, and the window then locked to itself, letterboxing the real picture). `video-params/*` is the family that genuinely appears only once the video is known: gate any read of the display pair on `video-params/w` first. The same belief in another guise — 「the properties do not exist until a frame is decoded」 — sat in the aspect poll's own comment for months, so don't trust a property-existence claim that a 404'd or remuxed file never got to exercise.

Two rules about the 10 Hz ticker:

- **It must not stop.** It drives `ViewModel.Tick()` (coalesced `percent-pos` seeks, stats refresh, volume push, the 「跳过」 button floating up) *and* pointer polling — which is how the player notices the hand moved after the cursor was hidden. `ChromeReveal.Pending` going false only means the OSD finished collapsing, not that the timer may stop. To save cost, make the idle tick cheap; don't stop the clock.
- **Don't `new` anything per tick.** Rebuilding a `TextBlock`, `FontFamily` or `Border` every second accumulates into visible GC pressure over a feature-length film; reuse the elements that are already there.

## Tests

`tests/EmbyNian.Tests` reaches Core only, which is why decisions belong there. Playback-adjacent suites: `PlaybackTests.cs`, `PinIndicatorTests.cs`, `PlayerPaletteTests.cs`, `ItemDetailTests.cs`, `StartupArgsTests.cs`, plus `SettingsTests.cs` for the settings and migration half. Add to these rather than opening a new file per change, and register any new suite in `Program.cs`.

When changing playback synchronisation, put these through the tests or the self-check: stop and close, episode switch, auto-next, resume position, progress reporting, server state write-back, and cancellation mid-flight.

Then run the gates — build, test and publish always, the self-check when a version ships (or when the check itself changed). The when and the procedure are in the `embynian-verification` skill.
