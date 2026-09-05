---
name: "mpv-shader-quality"
description: "EmbyNian's mpv picture-quality side — the loop for adding, swapping or iterating on a shader or a 档位 cell, how to read what a .glsl/.hook file actually does (self-gates, hook points, whether it scales at all), per-shader prerequisites, chain exclusivity, the scale-factor definition, the option-residue and \"UI lies\" traps, and how to inspect a live chain without starting real playback. Use when touching shaders, 画质档位, 配置组, scale/cscale/dscale, deband, upscaling or 色度重建 in the EmbyNian project."
---

# mpv Picture Quality in EmbyNian

Techniques and traps for the shader / 画质档位 side of the player, and the loop for adding, swapping or iterating on a shader. Facts about *specific files* belong in `assets/shaders/README.md` in the repo — that is the single source for provenance, licences, local edits and per-file gates. Don't restate per-file data here; go read it there.

## Read these first, in this order

1. `assets/shaders/README.md` — what each shipped file is, where it came from, and **which files gate themselves**.
2. `src/EmbyNian.Core/Mpv/` — `UpscaleTier.cs`, `ShaderGroup.cs`, `ShaderGroupCatalog.cs`, `ShaderChainRules.cs`, `ShaderLibrary.cs`, `ShaderSwitch.cs`. The live model. Type and member names in this skill may be stale; the code wins.
3. `画质档位重构-任务书.md` at the repo root — the decisions behind the current shape, plus what changed from the earlier plan.

**The standing rules are in `CLAUDE.md`, which is loaded whenever this skill is, and it outranks this file on every conflict** — the prime directive, the four gates, no real playback while verifying, credentials. `PROGRESS.md`'s 在途工作 section says what the current phase is about. Playback itself is the `embynian-playback` skill; the gate procedure is `embynian-verification`.

**There are no named 配置组 any more.** Until 2026-09-03 there were five hand-named groups picked by 片源分辨率. Now a `ShaderGroup` is *one cell of the 档位表*: `live|anime` × scale tier, with 显卡档 selecting a different chain behind the same id. So "make a new group" is really one of three different jobs — swapping a shader inside a cell (the usual one), bringing in a file and deciding which cells it belongs in, or letting the user assemble and save a chain of his own, which **does not exist**: that is a feature request, not an edit.

## Adding or swapping a shader — the loop

1. **Get the file in.** `assets/shaders/<vendor>/`, upstream filename and extension unchanged (ravu ships `.hook`), and its row in that README — upstream URL, licence, date, and any local edit — in the same change. No licence statement upstream means don't ship it.
2. **Read it before placing it** (next section). Does it scale, what is its gate, which hook point, which plane, is it multi-pass?
3. **Place it, then check the gate against the tier.** A shader whose gate never opens for a cell's factor range is a no-op in that cell — that is how `FSRCNNX_x1` and `CAS` got proposed for tiers they could never act in.
4. **Its prerequisites travel with it.** Chain options are derived from the chain in the catalog rather than written per cell, so teach the derivation once instead of copying options into every cell.
5. **Two lists must not drift**: the csproj copy list and the C# table. A test pins them and names the offending file when it fails.
6. **New mpv option → `NeutralOptions` entry + the both-directions round-trip test.** No exceptions; see the traps below.
7. **Four gates, then eyes.** Gates green first; then A/B on screen with the chain readout visible. You cannot judge the picture — he can.

## Never judge a shader by its filename

Three separate mistakes here came from reading the name instead of the file: `FSRCNNX_x1` shipped for a long time as the low-resolution "upscaler" and does not upscale at all; `CAS` was proposed for a matrix where its gate can never fire; `Ani4Kv2_ArtCNN_C4F32_i2` turned out to be a third-party repack of upstream ArtCNN carrying a CC BY-NC weight licence. Open the file — four things are readable at the top of each pass:

- `//!WIDTH` / `//!HEIGHT` — whether it changes resolution and by how much (`LUMA.w 2.0 *` is a 2× doubler). **No such directive anywhere in the file means it does not scale**, whatever the name says.
- `//!WHEN` — its gate. `OUTPUT.w LUMA.w / 1.3 >` means it silently does nothing below 1.3×.
- `//!HOOK` — which stage and plane it runs on (`LUMA`, `CHROMA`, `MAIN`, `SCALED`, `POSTKERNEL`…).
- the last pass — in a multi-pass network this is where the output size is actually decided.

## Being in the chain is not the same as running

Several shipped files gate themselves, so a chain that looks right on paper can be partly inert. Check each candidate's gate against the tier's factor range before putting it in a cell. Don't add C# branches duplicating a gate the file already has — that gate is the reason a wrong factor can't produce "an upscaler running on a shrinking picture".

## Order

Only the order *within one hook point* comes from the chain list; mpv's renderer fixes the rest, and the repo's own conclusion (see `ShaderGroup.cs`) is that every LUMA hook runs before every CHROMA hook, both before POSTKERNEL, POSTKERNEL before SCALED. So "written first in the list" does not mean "runs first" — write the list in pipeline order anyway, because that is what makes 「hdeband 必须在最前面」 true where it matters (hdeband shares the LUMA hook with the luma upscaler) and readable everywhere else. A chroma-from-luma shader therefore reads luma *after* a doubler has run, which is what you want; if you ever doubt an ordering, measure it and pin the conclusion in a test rather than a comment.

## Prerequisites are part of the shader, not decoration

- `SSimDownscaler` needs `dscale=mitchell` and `linear-downscaling=no`. Without them it is worse than not using it at all.
- `hdeband` requires mpv's built-in `deband` off; running both fights itself.
- An upscaler does spatial reconstruction only. Never add a gamma↔linear conversion because of which upscaler got picked, and never infer colour behaviour from a shader's name.

## One of each

Per automatic chain: at most one top-level luma upscaler, at most one post-sharpener, at most one standalone denoiser. Multi-pass inside a single shader package counts as one — judge by logical role, not by counting pass files.

## Scale factor

Factor = actual render-target height ÷ source height. **Output means the renderer's target, not the monitor**: a 1080p file in a small window on a 4K screen is not 2×. Source resolution alone cannot pick a chain — that was this subsystem's central bug. Source *quality* (noise, banding, blocking) is a separate axis from scale factor: decide deband/denoise from source height, otherwise the same DVD gets different treatment in PAL and NTSC because their factors land in different tiers.

## Two traps that have already bitten

- **Option residue.** Every mpv option any chain sets must appear in the `NeutralOptions` restore table, with the round-trip test in both directions. Miss one and switching away from that chain leaves the option — possibly a whole shader file — still in effect.
- **The UI lying.** Quality presets and chains both wrote `scale`/`cscale`/`dscale`; the chain is applied last, so the chain always won while the settings page kept displaying the preset. Whenever two layers can write the same mpv option, one of them must stop, and a test should assert what the renderer actually ends up with rather than what the decision layer intended.

## Inspecting a chain without playing anything real

Never verify this against the live Emby server — `CLAUDE.md` says why. Use the external `mpv.exe` backend with a **local file** instead:

- mpv's stats page lists every pass with its output size. That answers "is this shader running at all", "at what size" and "how expensive is it" directly. If you ever need a cost number, use measured pass times; don't invent cost tiers.
- `screenshot window` captures the rendered result including shaders, so an A/B is two PNGs.
- `--msg-level=vo/gpu=v` shows hook resolution and shader compile failures.

Whether the picture actually looks better is the user's call, not yours — every real defect in this area was caught by him looking at the screen. Ship a switchable A/B plus an on-screen readout of which chain is live, then ask him.

## Upstream

ArtCNN (Artoriuz, MIT) and the igv / agyild gists are maintained; Anime4K stopped in 2021 but its multi-step Mode A is still useful at large factors. ravu (bjin/mpv-prescalers, LGPL) ships `.hook`, not `.glsl` — keep upstream filenames so the next update can be diffed, and prefer the `-ar` anti-ringing variants. ArtCNN's `Chroma` models are ONNX-only, so GLSL chroma reconstruction means CfL. One shipped file already needs a local `#define` re-applied on every update, which is why the README records local edits per file.

Shader size is not a constraint in this project: the publish directory is around 300 MB and `libmpv-2.dll` alone is 117 MB. Never argue for dropping a capability on file-size grounds.
