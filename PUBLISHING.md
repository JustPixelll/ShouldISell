# Should I? — official Dalamud submission

Target: **new plugin → `testing/live`**.

## Repository readiness

- [x] Public source repository.
- [x] `Dalamud.NET.Sdk/15.0.0`.
- [x] Deterministic semantic version kept in the project file.
- [x] Release dependency lock committed.
- [x] Dalamud Windowing API for plugin windows.
- [x] Square `images/icon.png` in accepted size range.
- [x] Name / Author / Punchline / Description / RepoUrl / IconUrl metadata.
- [x] No automatic buying, selling, repricing, or queued native Market Board requests.
- [x] First-run setup available via `/shouldi setup`.
- [x] Item tooltip integration is optional/additive.
- [x] Inventory lookup uses Dalamud `IContextMenu`.

## Human test pass

Before approval, personally verify fresh install/setup, each module command, inventory/retainer observation, Universalis refresh scopes, Sell/Buy/Craft/Gather/Should I Do?/Tycoon, Market Board purchase tracking, vendor purchase recording, item tooltip coexistence, context-menu coexistence, unload/reload behavior, world/character switching and Dalamud logs.

## AI disclosure

Current Dalamud policy requires disclosure beyond basic autocomplete. Suggested PR disclosure:

> **AI usage: Copilot.** The maintainer designed the product, specified features and economic behavior, directed implementation, reviewed iterations, and personally tests/maintains the plugin. AI tooling implemented and refactored substantial portions of code and documentation under that direction.

The current plugin icon was created with AI assistance, so the user-visible plugin Description explicitly discloses this as required by the asset policy.

## D17 manifest

```toml
[plugin]
repository = "https://github.com/JustPixelll/ShouldISell.git"
commit = "REPLACE_WITH_FINAL_TESTED_COMMIT_SHA"
owners = ["JustPixelll"]
project_path = "ShouldISell"
changelog = "Initial testing release of Should I?: Sell, Buy, Craft, Gather, Should I Do?, Tycoon, native item-tooltip insights and inventory lookups."
```

## PR description

```markdown
## Should I?

FFXIV economy decision support combining Sell, Buy, Craft, Gather, Should I Do? and personal Tycoon analytics. It uses Universalis plus normal locally exposed inventory/Market Board observations. It does not automatically buy, sell, reprice, or queue native Market Board searches.

### AI usage
**Copilot.** The maintainer designed the product, specified features and economic behavior, directed implementation, reviewed iterations, and personally tests/maintains the plugin. AI tooling implemented and refactored substantial portions of code/documentation under that direction.

### Asset disclosure
The plugin icon was created with AI assistance; this is also disclosed in the user-visible plugin description.
```
