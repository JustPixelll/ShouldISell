# Third-party notices

Should I Sell? is an independent third-party project.

It interoperates with or references:

- **Dalamud** — plugin framework and public plugin services.
- **FFXIVClientStructs** — native FFXIV client structure definitions used for experimental live Market Board request handling.
- **Universalis** — public Market Board API used for current/history fallback and deeper historical analysis.

No affiliation or endorsement by Square Enix, XIVLauncher, Dalamud, FFXIVClientStructs, or Universalis is implied.

See the respective upstream projects for their own licenses and terms.
## FFXIV retainer sale-history packet research

The exact personal sale-history capture in v0.8.1 uses a packet layout and processing signature independently implemented from publicly documented FFXIV reverse-engineering work, including Ascended Ledger (jkleinne) and the CashFlow research it credits (NightmareXIV). No third-party source code is bundled. This integration is version-sensitive and degrades safely if the game signature changes.
