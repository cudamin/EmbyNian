---
name: "mpv-shader-quality"
description: "Momoka's mpv picture-quality side — the loop for adding, swapping or iterating on a shader or a 档位 cell, how to read what a .glsl/.hook file actually does (self-gates, hook points, whether it scales at all), per-shader prerequisites, chain exclusivity, the scale-factor definition, the option-residue and \"UI lies\" traps, and how to inspect a live chain without starting real playback. Use when touching shaders, 画质档位, 配置组, scale/cscale/dscale, deband, upscaling or 色度重建 in the Momoka project."
---

# mpv Picture Quality in Momoka

Techniques and traps for the shader / 画质档位 side of the player, and the loop for adding, swapping or iterating on a shader. Facts about *specific files* belong in `assets/shaders/README.md` in the repo — that is the single source for provenance, licences, local edits and per-file gates. Don't restate per-file data here; go read it there.

## Read these first, in this order

1. `assets/shaders/README.md` — what each shipped file is, where it came from, and **which files gate themselves**.
2. `src/Momoka.Core/Mpv/` — `UpscaleTier.cs`, `ShaderGroup.cs`, `ShaderGroupCatalog.cs`, `ShaderChainRules.cs`, `ShaderLibrary.cs`, `ShaderSwitch.cs`. The live model, and where the decisions behind the current shape are recorded (the file-level comments). Type and member names in this skill may be stale; the code wins.

**The current policy is in [CLAUDE.md](../../../CLAUDE.md)**: verification gates, playback authorization and probe coverage, credentials, and upgrades. This skill adds shader-specific measurements, not a second policy. `PROGRESS.md` records current work and earlier measurements; playback is `momoka-playback`, evidence is `momoka-verification`.

**There are no named 配置组 any more.** Until 2026-09-03 there were five hand-named groups picked by 片源分辨率. Now a `ShaderGroup` is *one cell of the 档位表*: `live|anime` × scale tier, with 显卡档 selecting a different chain behind the same id. (Two more axes swap the chain without entering the id, because they follow the source rather than the user's choice: `Vintage` for 老片源 ≤576 lines and `FastMotion` for 高帧率 >30fps — check `ShaderGroupCatalog` for the current set.) So "make a new group" is really one of three different jobs — swapping a shader inside a cell (the usual one), bringing in a file and deciding which cells it belongs in, or letting the user assemble and save a chain of his own, which **does not exist**: that is a feature request, not an edit.

## Adding or swapping a shader — the loop

1. **Get the file in.** `assets/shaders/<vendor>/`, upstream filename and extension unchanged (ravu ships `.hook`), and its row in that README — upstream URL, licence, date, and any local edit — in the same change. No licence statement upstream means don't ship it.
2. **Read it before placing it** (next section). Does it scale, what is its gate, which hook point, which plane, is it multi-pass?
3. **Place it, then check the gate against the tier.** A shader whose gate never opens for a cell's factor range is a no-op in that cell. Distinguish a gate that opens in only part of the range from a whole-cell no-op.
4. **Its prerequisites travel with it.** Chain options are derived from the chain in the catalog rather than written per cell, so teach the derivation once instead of copying options into every cell.
5. **The C# 档位表 and the shipped files must not drift**: Core tests compare the catalogue against GLSL/HOOK sources; `verify-publish.ps1` separately hashes every shipped shader and licence/notice text against the repository. Exercise missing HOOK, corrupt and extra files with `test-shader-publish.ps1`. A complete source tree does not prove a complete release folder.
6. **New mpv option → `NeutralOptions` entry + the both-directions round-trip test.** No exceptions; see the traps below.
7. **Run the applicable gates from CLAUDE.md, then inspect the permitted local render.** An A/B needs a visible chain readout, so the user can judge the picture while the assistant verifies which processing actually ran.

## Never judge a shader by its filename

Three unshipped files illustrate different checks: `FSRCNNX_x1` does not upscale at all despite being proposed as a low-resolution upscaler; `CAS` can run at native 1:1 within 缩小档 but cannot cover that whole tier (see the shader README); `Ani4Kv2_ArtCNN_C4F32_i2` is a third-party repack of upstream ArtCNN carrying a CC BY-NC weight licence. Open the file — four things are readable at the top of each pass:

- `//!WIDTH` / `//!HEIGHT` — whether it changes resolution and by how much (`LUMA.w 2.0 *` is a 2× doubler). **No such directive anywhere in the file means it does not scale**, whatever the name says.
- `//!WHEN` — its gate. `OUTPUT.w LUMA.w / 1.3 >` means it silently does nothing below 1.3×.
- `//!HOOK` — which stage and plane it runs on (`LUMA`, `CHROMA`, `MAIN`, `SCALED`, `POSTKERNEL`…).
- the last pass — in a multi-pass network this is where the output size is actually decided.

## Being in the chain is not the same as running

Several shipped files gate themselves, so a chain that looks right on paper can be partly inert. Evaluate gates against the input **after earlier shaders changed its dimensions**, not only against the original source. `NATIVE_CROPPED` can already be prescaled. A whole-cell no-op should leave that cell; a shader that executes in part of a cell may stay with its range documented. Preserve file-level gates rather than copying each threshold into runtime C# switches. `vo-passes` must distinguish the shader's algorithm from intermediate passes the presence of a hook creates; no algorithm pass does not prove zero overhead.

## Order

Within one hook point, the chain list controls execution order; the renderer fixes the order between stages. LUMA hooks run before CHROMA, both before POSTKERNEL, and POSTKERNEL before SCALED. Keep the list in pipeline order: hdeband must precede the luma upscaler because they share the LUMA hook.

**Execution order and texture binding are separate.** The RAVU/CfL comparison in the [shader README](../../../assets/shaders/README.md) and `ShaderGroup.cs` records that the list still determines which LUMA texture CfL binds across hook stages. CfL listed after the upscaler reads enlarged luma; listed before it, CfL reads original luma and leaves `cscale` to finish the job. Keep CfL after the luma upscaler. When changing that order or the renderer, verify the bound and output sizes as well as pass order; CHROMA executing later does not by itself prove which luma it read.

## Prerequisites are part of the shader, not decoration

- `SSimDownscaler` needs `dscale=mitchell` and `linear-downscaling=no`. Without them it is worse than not using it at all.
- `hdeband` requires mpv's built-in `deband` off; running both fights itself.
- An upscaler does spatial reconstruction only. Never add a gamma↔linear conversion because of which upscaler got picked, and never infer colour behaviour from a shader's name.

## One of each

Per automatic chain: at most one top-level luma upscaler, at most one post-sharpener, at most one standalone denoiser. Multi-pass inside a single shader package counts as one — judge by logical role, not by counting pass files.

## Scale factor

`ShaderTier.Measure` uses the smaller of output-width/source-width and output-height/source-height. Current Shell policy prepares a fixed **full-screen monitor target**, not the small window's current area, to avoid repeated cold compilation. Launch tickets and the UI must use the same target and the final successful media source. A page detaching for standalone playback must not discard its window-owned display measurement. Preferences are snapshotted for the current playback; moving monitors does not adopt settings promised for the next film. The renderer's actual current size still controls per-shader gates, so the chosen tier alone cannot prove a pass ran. Source quality (Vintage) remains independent of scale factor.

## Two traps that have already bitten

- **Option residue.** Every mpv option any chain sets must appear in the `NeutralOptions` restore table, with the round-trip test in both directions. Miss one and switching away from that chain leaves the option — possibly a whole shader file — still in effect.
- **The UI lying.** The old project-authored presets explicitly wrote `scale`/`cscale`/`dscale` and were then overwritten by the chain. Do not restore those hand-written preset scaler assignments. Built-in mpv profiles legitimately overlap with later options: the precedence is profile → user video options → chain prerequisites. Test the effective values and verify that removing the chain restores this playback's baseline, including expanded profile values. Use [momoka-video-output](../momoka-video-output/SKILL.md) for profile expansion, switch failures and restoration; overlapping option names alone are not a defect.
- **着色器 is off out of the box and 画质预设 is not its sub-option** (both the user's call, 2026-09-05). So 画质预设 is the first row of the 画质与着色器 card, above 启用着色器, and it is decided independently of that switch — a contract test pins that the `profile` handed to mpv does not change with the 启用着色器 state (the `default` preset sends no `profile` key at all; the others send `profile=<preset>`). Don't make the preset conditional on a chain existing, and don't reorder the card back.

## Inspecting a permitted local render

The following measurements apply to an isolated local-file run permitted by CLAUDE.md, not to the live Emby account. An external-player diagnostic must establish equivalent local-file and no-server isolation before use; these techniques are not an additional authorization. The integrated probes do not certify the external or standalone pipeline.

- mpv's stats page lists every pass with its output size. That answers "is this shader running at all", "at what size" and "how expensive is it" directly. If you ever need a cost number, use measured pass times; don't invent cost tiers.
- `screenshot window` captures the rendered result including shaders, so an A/B is two PNGs.
- Select the log module for the actual renderer. Current built-in pipelines force `vo=gpu-next`, so use `--msg-level=vo/gpu-next=v` for detailed rendering and compilation diagnostics; `--msg-level=vo/gpu=v` applies to `vo=gpu` and does not match `vo/gpu-next`. Read the diagnostics together with `vo-passes` to establish which algorithms executed.
- **Know what the fixture actually is before reading anything into a shot.** The `ffmpeg-probe` skill (user scope, `py <script>` — see `CLAUDE.md`) reads bit depth, chroma subsampling and HDR side data out of a file in one command, and `ffmpeg-hdr-color` covers PQ/HLG and tone mapping; a chroma-reconstruction or deband A/B against a source whose subsampling or transfer you guessed at proves nothing.

The user judges whether the picture is preferable; the assistant verifies that the intended chain ran and that the comparison used equivalent source, geometry and output conditions. Provide a switchable A/B with a visible chain readout rather than treating passing rules as proof of better image quality.

## Upstream

ArtCNN (Artoriuz, MIT) and the igv / agyild gists are maintained; Anime4K stopped in 2021 but its multi-step Mode A is still useful at large factors. ravu (bjin/mpv-prescalers, LGPL) ships `.hook`, not `.glsl` — keep upstream filenames so the next update can be diffed, and prefer the `-ar` anti-ringing variants. ArtCNN's `Chroma` models are ONNX-only, so GLSL chroma reconstruction means CfL. One shipped file already needs a local `#define` re-applied on every update, which is why the README records local edits per file.

Shader size alone is not a reason to drop a useful capability. Measure the current payload when it matters; dependency and payload trade-offs follow CLAUDE.md rather than a stale size estimate.
