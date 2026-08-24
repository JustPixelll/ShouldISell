# Should I? — Official Dalamud submission checklist

This file prepares the repository for an eventual official DalamudPluginsD17 submission. **Do not open the D17 submission PR until the local test checklist is complete.**

Current target: **new plugin → `testing/live`**.

## Repository readiness

- [x] Public GitHub repository.
- [x] Dalamud Windowing API used for plugin windows.
- [x] `Dalamud.NET.Sdk/15.0.0` project.
- [x] Deterministic semantic plugin version (currently `2.3.0.0`).
- [x] Square `images/icon.png` within the D17 size limits.
- [x] Plugin metadata: Name / Author / Punchline / Description / RepoUrl / IconUrl.
- [x] No queued native Market Board deep scanner in Should I?.
- [x] Experimental native queued scanning separated into the optional Should I Deep Mine? repository.
- [x] README documents current behavior, data sources, first setup, privacy and deliberate non-features.
- [x] First-run setup window available through `/shouldi setup`.
- [x] Native item-tooltip integration is optional and additive.
- [x] Inventory context-menu integration uses Dalamud `IContextMenu`.
- [ ] Commit the final generated `packages.lock.json` after the last dependency/SDK change.
- [ ] Replace screenshots with current v2.3 UI screenshots after the local test pass if existing screenshots are stale.

## Required human test pass before submission

Dalamud's AI policy requires the maintainer to personally test, understand and be able to explain the plugin.

- [ ] Fresh install with no prior Should I? configuration.
- [ ] First-run guide opens and can be completed/reopened.
- [ ] `/shouldi` and every module command open the expected view.
- [ ] Player inventory / saddlebags / retainer snapshots populate as documented.
- [ ] Inventory coverage warning clears on inventory open and stays gone after permanent dismissal.
- [ ] Universalis refresh scopes work without invoking native queued ItemSearch.
- [ ] Sell ratings/details/current listings behave normally.
- [ ] Buy Market Board and Vendor tabs run separately and filters behave as described.
- [ ] Market Board purchase tracking identifies a purchase made by the player.
- [ ] Manual Vendor opportunity → Tycoon purchase workflow records the chosen cost basis.
- [ ] Craft scan completes and ingredient routing is inspectable.
- [ ] Gather scan completes for MIN/BTN and does not claim the removed fake range.
- [ ] Opportunities merges available cached results.
- [ ] Tycoon cashflow / purchases / positions / sales pages open without errors.
- [ ] Normal FFXIV ItemDetail tooltip displays the compact Should I? block.
- [ ] Tooltip tested with at least one other tooltip-augmenting plugin enabled.
- [ ] Tooltip remains usable at different FFXIV UI scaling values.
- [ ] Right-click `Look up in Should I…` works alongside other context-menu plugins.
- [ ] Disable tooltip/context integration and confirm both stay disabled.
- [ ] Plugin unload/reload does not leave native tooltip nodes behind.
- [ ] World/character changes do not leak recommendations from the wrong context.
- [ ] Review Dalamud log for recurring errors/exceptions after a normal play session.

## AI disclosure — required

The development of Should I? has used substantial AI assistance. The D17 pull request must disclose the level of AI use according to the current Dalamud AI Usage Policy.

Suggested disclosure based on the current development workflow:

> **AI usage: Copilot.** The maintainer designed the product, specified features/economic behavior, reviewed iterations and personally tested the plugin. AI tooling implemented and refactored substantial portions of the code and documentation under that direction. The maintainer reviewed the submitted code and can explain/maintain the implementation.

Adjust that wording if the actual final human/AI contribution changes before submission. Never omit the disclosure.

### Icon / asset disclosure

The current v2.3 icon was produced with AI assistance during repository preparation. Dalamud's current policy requires AI-generated user-facing assets to be disclosed in the plugin description and explicitly prefers a human-made icon.

Before the D17 submission choose **one**:

1. **Preferred:** manually redraw/replace `images/icon.png` with a human-created asset; or
2. keep the current asset and add an explicit AI-asset disclosure to the plugin Description shown to users.

Do not submit the current AI-assisted icon without doing one of those two things.

## D17 manifest draft

The final commit must be the exact commit that the official builder should compile.

Create this file in your D17 fork:

`testing/live/Should I/manifest.toml`

```toml
[plugin]
repository = "https://github.com/JustPixelll/ShouldISell.git"
commit = "REPLACE_WITH_FINAL_TESTED_COMMIT_SHA"
owners = ["JustPixelll"]
project_path = "ShouldISell"
changelog = "Initial testing release of Should I?: Sell, Buy, Craft, Gather, Opportunities, Tycoon, native item tooltip insights and inventory lookups."
```

The D17 PR should target the new-plugin testing channel, not stable.

## Proposed D17 PR description

```markdown
## Should I?

FFXIV economy decision support combining Sell, Buy, Craft, Gather, Opportunities and personal Tycoon analytics. The plugin uses Universalis plus normal locally exposed inventory/Market Board observations. It does not automatically buy/sell/reprice items and does not contain the experimental queued native Market Board scanner; that scanner lives in a separate custom-repository companion and is not part of this submission.

### Test notes
- Fresh-install first-run flow tested
- Inventory / retainer observation tested
- Universalis refresh and module scans tested
- Native ItemDetail tooltip augmentation tested with another tooltip plugin
- Inventory context-menu coexistence tested
- Plugin unload/reload tested

### AI usage
**Copilot.** The maintainer designed the product, specified features/economic behavior, reviewed iterations and personally tested the plugin. AI tooling implemented and refactored substantial portions of the code/documentation under that direction. The maintainer reviewed the submitted code and can explain/maintain the implementation.
```

If the final icon remains AI-assisted, also include the required asset disclosure in the plugin's user-visible Description before submitting.

## Final pre-PR sequence

1. Finish the human test checklist above.
2. Resolve every issue found during that pass.
3. Update screenshots/README if the UI changed.
4. Final Release build.
5. Commit the generated `packages.lock.json`.
6. Confirm `images/icon.png` is the intended final human-created/disclosed asset.
7. Record the exact final commit SHA.
8. Put that SHA into the D17 `testing/live/Should I/manifest.toml`.
9. Open the D17 PR with the AI disclosure.
10. Respond to PAC/code-review feedback and test any requested changes locally before updating the commit SHA.
