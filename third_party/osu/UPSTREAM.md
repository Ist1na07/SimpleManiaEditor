# osu!mania rules reference

## 0.1.11 official runtime

Actual playtest now runs `ppy.osu.Game` and four official ruleset packages
2026.916.0, source `98fb49876c0242fcf649e0250d6f6b3458769a9e`, with
Framework 2026.914.0 (`59220cc25c394eddd78cdae9d3dfdee0a5caa0b3`) and
Resources 2026.909.0 (`742ca60d4e7273dce0cac2fced7cd132f26f11c8`).
`SimpleManiaEditor.OsuHost` runs OsuGame and a thin Player subclass in its own
SDL/GPU/audio process. `PracticePlayer` follows upstream EditorPlayer's clock,
autoplay and local practice hooks; it uses the public API, without copied
rendering, private reflection patches or a hand-written skin decoder. Prior
objects are removed only from the disposable handoff. The complete official
skin/UI/gameplay dependencies come from pinned NuGet packages, recorded in the
host lockfile. Old source references below document the previous adaptation,
which no longer runs from the editor's playtest button.

Game/framework MIT and resource CC BY-NC/font/native notices travel in the
package. Read `src/THIRD-PARTY-NOTICES.md` before public distribution.

0.1.10 additionally retains the pinned `LegacyManiaSkinDecoder.cs`, `LegacyNotePiece.cs`, and `LegacyHoldNoteTailPiece.cs` for auditable skin parsing and tail-anchor compatibility. OsuIniDocument adapts the repeated-Keys configuration flow; PlaytestSurface uses the official opposite direction for the hold tail. This remains an Avalonia adaptation, not a complete osu.Framework host.

- Repository: https://github.com/ppy/osu
- Commit: `7e25f111466f1b5648d86856e5737d852402effe` (2026-10-08 retrieval).
- Source: `osu.Game.Rulesets.Mania/Scoring/ManiaHitWindows.cs`.
- Local reference SHA-256: `58985770CB8730D4315CCE86C966BC751FCF2947EDC1DA483200C464AC331B70`.
- MIT license retained here and distributed as `licenses/osu-MIT.txt`.

`StableManiaRules.cs` adapts the native mania, Classic enabled, ScoreV2 disabled branch. This is not an embedded osu.Framework gameplay host, and is not osu! stable's closed-source binary.

ScoreV1 combined LN head/release rules, asymmetric late windows and accuracy come from the official [judgement documentation](https://osu.ppy.sh/wiki/en/Gameplay/Judgement/osu!mania). Base/bonus scoring without mods comes from the official [ScoreV1 documentation](https://osu.ppy.sh/wiki/en/Gameplay/Score/ScoreV1/osu!mania).

Scrolling follows the relative beat-length policy in `DrawableScrollingRuleset` / `DrawableManiaRuleset` at the pinned commit. This implementation integrates directly in song milliseconds, normalizes BPM by duration and retains chart SV. Since 0.1.9 the default speed control uses `DrawableManiaRuleset.ComputeScrollTime`: `11485 / speed` milliseconds for 1–40, with the default legacy hit-position scale 402/480 and playback-rate compensation. The old custom profile retains pixels/second.

Legacy skin naming/layout is independently implemented against the official [skin.ini specification](https://osu.ppy.sh/wiki/en/Skinning/skin.ini), [mania asset specification](https://osu.ppy.sh/wiki/en/Skinning/osu!mania), `LegacyManiaSkinConfiguration`, `LegacyManiaSkinDecoder`, and mania legacy skin components at the pinned commit. Stable user key layouts use the official [configuration specification](https://osu.ppy.sh/wiki/en/Client/Program_files/User_configuration_file). Official visual assets are not bundled; imported skins remain user-supplied. The new fullscreen surface is an Avalonia renderer, not an embedded osu.Framework host.

Regression fixtures use independent OD values, exact window boundaries, combined LN examples, 300/200 accuracy weights, perfect one-million score, pause-free input repeats, continuous SV integration, actual 7K center Space routing and a 100,000-note viewport.
