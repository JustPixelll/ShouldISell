# Third-party notices

Should I? is an independent third-party project.

It interoperates with or references:

- **Dalamud** — plugin framework and public plugin services.
- **FFXIVClientStructs** — native FFXIV client structure definitions used for selected game-UI/data integrations.
- **Universalis** — public Market Board API used for current/history market analysis.

No affiliation or endorsement by Square Enix, XIVLauncher, Dalamud, FFXIVClientStructs, or Universalis is implied.

See the respective upstream projects for their own licenses and terms.

## PriceInsight tooltip implementation reference

The native `ItemDetail` tooltip augmentation in Should I? was implemented with reference to the established node-insertion and AddonLifecycle approach used by **PriceInsight** by Kouzukii:

https://github.com/Kouzukii/ffxiv-priceinsight

PriceInsight is distributed under the MIT License:

> Copyright (c) 2021-2022 Kouzukii
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

Should I? owns a separate uniquely identified tooltip node and has no runtime dependency on the PriceInsight assembly.

## FFXIV retainer sale-history packet research

The exact personal sale-history capture in v0.8.1 uses a packet layout and processing signature independently implemented from publicly documented FFXIV reverse-engineering work, including Ascended Ledger (jkleinne) and the CashFlow research it credits (NightmareXIV). No third-party source code is bundled. This integration is version-sensitive and degrades safely if the game signature changes.
