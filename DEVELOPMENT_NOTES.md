# Build notes — v0.8.0

The ChatGPT execution environment used to assemble this source does not contain the .NET SDK or Dalamud runtime, so run the normal Windows build and send back compiler output if API 15 reports a binding mismatch.

## New API/UI touchpoints to validate

1. `ITextureProvider.GetFromGameIcon(new GameIconLookup(iconId, isHq))` + `TryGetWrap(...)` — item icon in the detail inspector.
2. `ImGui.Selectable(... SpanAllColumns | AllowItemOverlap ...)` — full-row hover/click hit target in tables.
3. `ImGui.TableSetBgColor(... RowBg0 ...)` — whole-row hover highlight.
4. Custom table header row using `ImGui.TableHeader(...)` + hover tooltips — verify sorting still responds normally to header clicks.
5. `ImGui.IsItemClicked(ImGuiMouseButton.Right)` + `ImGui.SetClipboardText(...)` — right-click Suggested price clipboard shortcut.
6. `ImGui.DragLong(...)` — expected-net / payout filter bounds.
7. Current Listings now uses 13 columns and horizontal scrolling.

## First validation cases

- Hover a row in both main tables: the entire row should highlight, not only the cell under the cursor.
- Click an item name, qty, median or other non-rating cell: the same detail inspector should open.
- Click the same row again or press **Back to full table**: the detail panel should disappear and the table reclaim the full height.
- Detail inspector should show the correct FFXIV item icon, including HQ lookup where applicable.
- Right-click a Suggested price, then paste into Notepad/market-board field: clipboard should contain digits only (for example `1234`, not `1,234g`).
- Header hover tooltips should explain every column while normal header sorting still works.
- Owned filters should correctly combine rating range, star range and Est. net range.
- Current Listings filters should combine rating range, star range and expected current payout range.
- Current Listings should show Listed qty, Listed price and Exp. payout; Exp. payout should equal `qty × floor(price × 0.95)`.
- With Meaningful listing value = 10,000g, a recommended stack of 1 × ~100g should receive very little Value credit even if the player owns 100+ units.
- A genuinely excellent high-value/liquid item can remain 5★ while displaying, for example, a strict score in the high 80s/90s.
- Open/rebuild Current Listings repeatedly and verify market depth does not shrink: own-listing exclusion now clones the market snapshot before removal.

## Score semantics changed in v0.8

`ValueThresholdGil` is retained for config compatibility, but its meaning is now **meaningful expected after-tax payout of one recommended listing**, not per-unit price and not whole-stock value. Existing numeric settings are intentionally preserved during migration.

The displayed numeric score is `SellRating.OpportunityScore` (strict/unexpanded). `RawScore` still drives the 1–5 star calibration. This separation is intentional.


## New v0.8 validation cases

- Open a retainer Market Board listing, return to Owned Items, and verify the matching item/HQ row is gold-tinted.
- For a case like 105 owned with recommended stack 1, `Est. net` should equal roughly one net unit, not 105 net units.
- The detail inspector should still show the full known-position value separately.
- The Value component should follow the recommended-listing payout.
- A recommendation requiring ~100 separate listings should receive a mild execution-friction note/penalty without automatically losing 5★ if the market itself is excellent.
