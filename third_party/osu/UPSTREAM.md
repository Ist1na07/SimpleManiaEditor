# osu!mania rules reference

- Repository: https://github.com/ppy/osu
- Commit: `7e25f111466f1b5648d86856e5737d852402effe` (2026-10-08 retrieval).
- Source: `osu.Game.Rulesets.Mania/Scoring/ManiaHitWindows.cs`.
- Local reference SHA-256: `58985770CB8730D4315CCE86C966BC751FCF2947EDC1DA483200C464AC331B70`.
- MIT license retained here and distributed as `licenses/osu-MIT.txt`.

`StableManiaRules.cs` adapts the native mania, Classic enabled, ScoreV2 disabled branch. This is not an embedded osu.Framework gameplay host, and is not osu! stable's closed-source binary.

ScoreV1 combined LN head/release rules, asymmetric late windows and accuracy come from the official [judgement documentation](https://osu.ppy.sh/wiki/en/Gameplay/Judgement/osu!mania). Base/bonus scoring without mods comes from the official [ScoreV1 documentation](https://osu.ppy.sh/wiki/en/Gameplay/Score/ScoreV1/osu!mania).

Scrolling follows the relative beat-length policy in `DrawableScrollingRuleset` / `DrawableManiaRuleset` at the pinned commit. This implementation integrates directly in song milliseconds, normalizes BPM by duration and retains chart SV. The speed control remains pixels/second, not the stable client's 1–40 preference scale.

Regression fixtures use independent OD values, exact window boundaries, combined LN examples, 300/200 accuracy weights, perfect one-million score, pause-free input repeats, continuous SV integration, actual 7K center Space routing and a 100,000-note viewport.
