# Third-party notices

LumaChart Studio's own code is MIT licensed; see `LICENSE`. The components
below are not, and their licences remain in effect. Two of them are copyleft
and place obligations on anyone who distributes a build of this software — see
"Copyleft components" before you ship a binary.

| Component | Licence |
| --- | --- |
| NAudio, NAudio.Vorbis | MIT |
| NLayer, NLayer.NAudioSupport | MIT |
| OggVorbisEncoder | MIT |
| WanaKana-net | MIT |
| Avalonia (and Avalonia.Skia, Win32, Android, iOS, Themes.Fluent, HarfBuzz) | MIT |
| SkiaSharp, HarfBuzzSharp | MIT |
| Xamarin.AndroidX.Core.SplashScreen (Android) | MIT AND Apache-2.0 |
| Kawazu | MIT — see the note below |
| **SoundTouch.Net, SoundTouch.Net.NAudioSupport.Core** | **LGPL-2.1-or-later** |
| **LibNMeCab, LibNMeCab.IpaDicBin** | **GPL-2.0-or-later OR LGPL-2.1-or-later** |
| IPADIC dictionary data (shipped inside LibNMeCab.IpaDicBin) | NAIST / ICOT — see below |
| Source Han Sans CN (Android, iOS) | SIL OFL 1.1 — see below |
| Inter, via Avalonia.Fonts.Inter (iOS) | SIL OFL 1.1, Reserved Font Name "Inter", © Rasmus Andersson |

**Kawazu.** The published NuGet package declares no licence: its `.nuspec`
carries a copyright line (`© Cutano 2020`) and nothing else. The upstream
repository at https://github.com/Cutano/Kawazu states MIT, and that is the
grant relied on here. If you fork this project and that matters to you, confirm
it at the source rather than from the package metadata.

No visual assets from Malody or osu! are distributed with this project. Product
names are used only to describe compatible chart formats.

## Copyleft components

**This project elects LGPL-2.1-or-later for LibNMeCab and LibNMeCab.IpaDicBin.**
They are offered under `GPL-2.0-or-later OR LGPL-2.1-or-later`, and the choice
is the distributor's to make. Electing the LGPL is what allows LumaChart
Studio's own code to stay MIT; electing the GPL instead would require the whole
combined work to be GPL.

SoundTouch.Net is LGPL-2.1-or-later with no alternative offer.

Because of these two, an **MIT grant on this repository does not make a binary
built from it freely redistributable on MIT terms**. If you distribute such a
binary you take on the LGPL's obligations, the substantive one being that
recipients must be able to relink your application against a modified version
of the LGPL component. In practice:

- **Publish your source, or otherwise provide what is needed to relink.** The
  Android and iOS clients are AOT compiled, so the LGPL parts are linked
  statically rather than as replaceable shared libraries. Making the
  corresponding source of your build available is the straightforward way to
  satisfy this; shipping object files is the alternative the licence also
  allows.
- **Carry the licence text and state which components are LGPL.** This file
  does that for the upstream project.
- **Keep modifications to those components under the same licence**, and say
  what you changed. See "Modified dependencies".

None of this restricts using, forking or modifying the source in this
repository. It applies at the point you hand someone a compiled application.

## Japanese dictionary data (IPADIC)

Romanising Japanese titles uses Kawazu, which reads the **IPADIC** corpus
through LibNMeCab.IpaDicBin. That dictionary data is distributed inside all
three clients: as NuGet content on Windows, as an `AndroidAsset` on Android,
and as a `BundleResource` on iOS.

IPADIC is © 2000–2003 Nara Institute of Science and Technology, and a large
portion of its entries originate from ICOT Free Software. Both licences require
that the copyright notice and the no-warranty provisions accompany **every**
copy, original or modified. The complete text is included in this repository
and ships with every client at:

- `licenses/IPADIC-COPYING.txt`
- `licenses/IPADIC-AUTHORS.txt`

Those two files are byte-for-byte as distributed upstream and must not be
edited, reflowed or re-encoded — including the stray trailing bytes in
`IPADIC-COPYING.txt`, which are present in the upstream package.

## Modified dependencies (iOS client only)

The iOS client ships two dependencies rebuilt from their own sources with a
local change. Both remain under their upstream licences; the modifications are
kept as patch files in this repository so anyone can see exactly what differs,
and neither is redistributed as, or presented as, the upstream package.

- **Avalonia** (MIT) — `tools/avalonia/`. `DisplayLinkTimer` is changed to drive
  the render loop from the main run loop. iOS 26 refuses off-main-thread changes
  to a view's layer properties, which left every Avalonia app drawing into a
  layer that was never attached: a black screen with no error.
- **LibNMeCab** (GPL-2.0-or-later OR LGPL-2.1-or-later; used here under
  LGPL-2.1-or-later) — `tools/nmecab/`. `StrUtils.GetEncodingOrNull` is changed
  to compare encoding names without `CompareOptions.IgnoreSymbols`, which .NET
  on Apple platforms does not support. Without it the Japanese dictionary
  cannot be opened at all. The patch file is the required statement of what was
  modified, and the modified library remains under its own licence.

The Windows and Android clients use the unmodified packages.

## Bundled font (mobile clients)

The Android and iOS clients embed **Source Han Sans CN (思源黑体 CN)**, Regular
weight (Regional Subset OTF), so Chinese text renders identically on every
device instead of depending on the device's own fonts. The desktop (Windows)
client does not bundle it and resolves system fonts instead.

Source Han Sans is © 2014–2025 Adobe (https://github.com/adobe-fonts/source-han-sans),
licensed under the SIL Open Font License, Version 1.1, with Reserved Font Name
"Source". The font is used and redistributed unmodified. Under the OFL it may
be bundled with, and redistributed inside, application software.

The OFL requires its text to travel with the font rather than merely be linked.
The complete licence, as distributed by Adobe, is included at
`licenses/OFL-1.1-SourceHanSans.txt` and ships with every client that bundles
the font. Note the Reserved Font Name clause: a modified version of this font
may not be distributed under the name "Source Han Sans" or any name containing
"Source".

## Sunny Rework / osu!mania toolkit

The 4K Rework star estimator and Azusa RC reference grader in `LumaChart.Core`
are clean C# ports of the implementations distributed by
`LeoBlackMT/nonebot-plugin-osumania-toolkit`. Sunny identifies
`sunnyxxy/Star-Rating-Rebirth` as its algorithm source. The Azusa reference
grader was aligned against toolkit version 1.1.3. No Python runtime, Python
package, ONNX model or external ETT runner is included or invoked by LumaChart
Studio.

Copyright (c) 2026 Leo_Black

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
