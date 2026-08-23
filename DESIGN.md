# Should I Sell? — agreed design

## Product question

For every marketable item known to be in player inventory, saddlebag, retainer inventory, or a retainer's current market listings, answer:

> **How attractive is it to sell this item right now?**

The addon does **not** decide whether the player has enough retainer slots or whether Item A should replace Item B. Each item is rated independently. The user decides what to post.

## User input

There is one subjective input to the rating model: **Meaningful listing value (gil)**.

- It means the expected **after-tax gil payout of one recommended listing** that the user considers meaningfully worth the selling effort.
- At exactly this value, the Value component is neutral (50%).
- Roughly 10× the reference is strongly positive; roughly 0.1× is strongly negative.
- It is a smooth logarithmic curve, not a hard minimum.
- The recommended stack size therefore directly affects value. Owning 105 units does not create a 105-unit value event when the model recommends selling them one-at-a-time.
- Plans requiring very large numbers of separate listings receive a mild logarithmic execution-friction penalty.

Technical settings such as cache TTL, stale age, retry count, and request spacing are operational controls and do not express a market preference.

## Data sources

### FFXIV client / Dalamud

Used for:

- Player inventory and loaded saddlebags.
- Active retainer inventory pages.
- Active retainer's `RetainerMarket` container.
- HQ/NQ state and quantities.
- Passive observation of actual current market-board listing packets.
- Passive observation of the game's recent sale-history packet.
- Experimental direct stale-item lookup via `InfoProxyItemSearch.RequestData()`.

FFXIV does not keep every retainer inventory loaded simultaneously. Therefore the addon snapshots a retainer when its containers are loaded and persists that snapshot locally. An unloaded container does not erase its previous snapshot.

### Universalis

Used for:

- Current server listings for owned item IDs.
- Deeper historical sold-price data than the game UI normally exposes.
- Crowdsourced `lastUploadTime` / market-observation freshness.

Requests are batched by unique item ID, up to 100 IDs per request. HQ/NQ remains a rating dimension rather than causing duplicate network requests.

### Lumina / game sheets

Used for static metadata:

- Item name.
- Whether an item participates in market-board search (`ItemSearchCategory`).
- HQ capability.
- Vendor price metadata for later vendor-comparison rules.

## Source priority

For current listings:

1. Live FFXIV observation from this client.
2. Recent local live-game cache.
3. Universalis current market data.

A stale Universalis response must never overwrite a newer direct game observation.

For sale history, observations are merged and duplicate-looking transactions collapse conservatively.

## Freshness vs demand

These are separate concepts:

- **Listing freshness:** when somebody last observed the current market.
- **Last sale age:** when somebody actually bought the item.

Example: listings observed 2 minutes ago + last sale 47 days ago = fresh data showing extremely low demand. It is **not** a reason to refresh the item over and over.

## Raw datapoints

Current-market datapoints:

- Lowest listing price.
- Full available listing ladder.
- Quantity per listing.
- Total units for sale.
- Number of listings.
- Unique retainers/sellers when available.
- HQ/NQ.
- Listing observation timestamp.

Historical SOLD datapoints:

- Actual sale price.
- Sale timestamp.
- Quantity sold.
- HQ/NQ.
- Number of transactions.
- Last actual sale age.

Inventory datapoints:

- Item ID.
- HQ/NQ.
- Quantity.
- Owner/location (player or which retainer).
- Container.
- Snapshot age.

## Derived statistics

### Historical market value

Primary statistics are based on **sold prices**, not listing prices:

- Recency-weighted median.
- Q1 (25th percentile).
- Median.
- Q3 (75th percentile).
- 7-day median.
- 30-day median.

The normal arithmetic mean is not the primary benchmark because a small number of extreme transactions can distort it badly.

### Price attractiveness

Compare the realistic current listing price with the recency-weighted sold median.

A current price well above normal sold value is a positive signal. A current price well below it is a negative signal.

### Isolated undercuts / listing depth

The cheapest listing is not automatically the true current market.

Example:

- 9,000 x1
- 14,000 x10
- 14,100 x10
- 14,200 x10

with a historical median near 14,000 should not be treated as a 9,000-gil market. v0.1 contains a deliberately conservative cluster heuristic; this should be tuned with real observations.

### Demand

Keep both:

- **Units/day** — total quantity moving.
- **Transactions/day** — how frequently separate purchases occur.

A single stack of 99 every few days is not behaviourally identical to many small purchases each day.

### Supply pressure

Approximate days of currently listed supply:

`current listed units / units sold per day`

Low days-of-supply is favourable. Huge days-of-supply is unfavourable.

### Liquidity / approximate time to sell

Approximate stock ahead of a realistically priced listing:

`units at or below realistic price / units sold per day`

This is not a promise of sale time; it is a queue-pressure heuristic.

### Competition

Future scoring can additionally model:

- Unique sellers/retainers.
- Listing count.
- Stock concentration by seller.
- Undercut/repricing frequency if a trustworthy time series is available.

v0.1 captures seller IDs/names but does not yet give seller concentration its own weight.

### Stability

Use relative interquartile range:

`(Q3 - Q1) / median`

A narrow sold-price distribution means the market value estimate is more stable. A wild distribution reduces stability.

### Trend

Compare shorter and longer sold-price windows, beginning with:

`7-day median / 30-day median`

This gives a simple falling / stable / rising signal.

### Stack behaviour and recommendation

Historical sale quantities are used to recommend a practical stack size. The engine considers transaction frequency by stack size, burst-adjusted buyer behavior, total purchase spend, time-normalized convenience premiums/bulk discounts, sell-through speed, current liquidity, total quantity owned, actual item stack limit, and a fragmentation/manual-management penalty.

The engine returns both a best-balance recommendation and a low-maintenance alternative. Suggested price and suggested stack are solved jointly because small-stack convenience premiums can support a different unit price than a bulk stack.

## Stars and confidence

Stars answer **selling opportunity**, not “how expensive is this item?”

- ★☆☆☆☆ — poor selling opportunity.
- ★★☆☆☆ — weak.
- ★★★☆☆ — normal / reasonable.
- ★★★★☆ — good.
- ★★★★★ — excellent current selling opportunity.

A 700,000-gil item with awful liquidity and a weak current price can rate below a 15,000-gil material with excellent demand and price conditions.

**Confidence is separate.** A five-star conclusion based on two ancient sales should visibly have low confidence rather than pretending the evidence is strong.

## Current weighted score (v0.8)

- Price attractiveness: **25%**
- Demand: **17%**
- Supply: **12%**
- Liquidity: **11%**
- Stability: **9%**
- Trend: **5%**
- Expected recommended-listing value: **11%**
- Vendor economics: **10%**

Stars and the numeric score deliberately use different calibration strictness. Stars use contrast expansion so excellent opportunities can reach 5★ without every component being perfect. The 0–100 opportunity score uses the stricter unexpanded signal (plus the same vendor safeguards), keeping 100 rare and meaningful.

Vendor economics also applies stronger post-score safeguards when after-tax market proceeds fail to beat guaranteed NPC buyback, and a bounded evidence-gated convenience-arbitrage bonus when actual recent sales support a market premium over a normal gil vendor.

## Experimental stale-item updater

### Goal

Universalis may have very old data for an item that few people search. If the player owns it, the addon can deliberately ask FFXIV for that item's market data.

### Queue

1. Build unique marketable item IDs from known owned-item snapshots.
2. Read the freshest known listing observation per item.
3. Exclude items newer than the configured stale threshold.
4. Queue the rest.
5. Set the native `InfoProxyItemSearch.SearchItemId`.
6. Call the native `RequestData()` path.
7. Wait for Dalamud `IMarketBoard.HistoryReceived` plus the subsequent offering pages to settle.
8. Store live FFXIV data immediately.
9. Cool down before the next item.
10. Retry timeouts up to the configured maximum; then skip instead of looping forever.

The updater sends one item request at a time and defaults to a multi-second spacing.

### Universalis contribution

The addon intentionally does not construct its own Universalis upload. Dalamud already has market-board collection/upload logic for normal observed market requests when the user's relevant Dalamud setting is enabled. Our local addon does not need to wait for Universalis to reflect the upload before rating the item: the game response itself is already the freshest source.

## Persistence

v0.1 uses one JSON document in the Dalamud plugin config directory. This is intentionally simple for the first experiment.

A later migration to SQLite is sensible if we decide to retain a long local time series of listing snapshots, because that unlocks seller concentration, repricing frequency, and richer market-change analysis without ballooning one JSON file.

## UI layers

### Built in v0.1

Own addon window containing:

- All known owned item/HQ variants.
- 1–5 stars.
- Current realistic price.
- Historical median.
- Units/day.
- Confidence.
- Data age.
- Detailed score breakdown.
- Experimental refresh progress.
- Meaningful listing value (gil).

### Next native integration layer

The score/cache service is deliberately UI-independent so the next milestone can render the same rating in:

- Player inventory.
- Saddlebag.
- Retainer inventory.
- Retainer market listings.
- Item tooltip.
- Market-board views where appropriate.

No market calculations should run inside a native UI draw hook; UI hooks should only ask the local score cache for the already-computed result.

## Later extensions / ideas beyond the current v0.8 core

- Exact retainer-city seller-tax lookup instead of the conservative 5% assumption.
- Native rating overlays in FFXIV inventory/retainer rows and item tooltips.
- Craft-vs-sell / keep-for-crafting opportunity cost.
- NPC replacement/arbitrage signal.
- Cross-world or data-center markets.
- Local listing-snapshot time series and undercut frequency.
- Empirically calibrated score curves after collecting real examples.


## v0.2 first-test corrections

- The UI exposes **Live Scan Open Sell Inventory**. It detects `Inventory`, `RetainerGrid0..4` / `RetainerCrystalGrid`, or `RetainerSellList` and force-queues every unique marketable item in that current scope. This is intentionally different from the stale-only refresher.
- Owned Items columns are sortable by Rating, Item, Qty, Current, Median, Units/day, Confidence, and Freshness.
- The sole subjective input is now a literal gil amount. The amount is the 50% midpoint of the absolute-value score on a logarithmic curve.
- Final 1–5 conversion applies a 2.20x contrast around the neutral weighted score. This directly fixes the v0.1 mathematical issue where linear rounding made 1★ and 5★ almost unreachable for naturally middle-clustered component scores.


## Rating transparency (v0.3)

The main table displays both stars and the calibrated numeric rating on a 0–100 red/yellow/green scale. Hovering the rating exposes each raw component score, its model weight, its weighted contribution, the pre-calibration weighted total, final calibrated score, and confidence. This is intentionally diagnostic so empirical tuning can be based on visible causes rather than opaque star changes.

Estimated sale value is presented as both per-unit and total-owned value. From v0.4 onward it prefers the suggested executable listing price, and from v0.5 that price is coupled to the recommended stack size when quantity history supports a premium/discount. Historical median remains the fallback when no executable suggestion exists.


## Executable suggested price (v0.4)

The advertised lowest/current board price and the price we expect the player can reasonably list at are separate concepts. The suggestion engine:

1. Anchors on recency-weighted actual sold prices and Q1/Q3.
2. Rejects unsupported fantasy asks that sit far beyond the historical distribution unless multiple recent sales support that level.
3. Groups current listings into price tiers and estimates how quickly cumulative cheaper depth should clear from units/day.
4. May skip shallow cheap tiers (for example 2 units in front of a 99-unit stack in a high-volume market) when those units should clear quickly.
5. Uses quantity-for-sale / units-per-day to become more conservative for large positions.
6. Caps optimistic prices by recent sold-price evidence.

The 1–5 rating uses this executable suggested price for its price-attractiveness and absolute-value components. Current board asks remain visible as context/anomaly signals.

## Current personal listings

When a retainer is loaded, the plugin snapshots `RetainerMarket` and reads each market slot price. It persists first observation, price-change observation and last-seen time by retainer + market slot + item/HQ. This supports the Current Listings tab. `FirstSeenUtc` is not claimed to be the true FFXIV listing creation time; it is the earliest local observation.


## v0.5 joint price + stack recommendation

The recommendation layer now evaluates candidate **(stack size, unit price)** pairs rather than treating stack size as cosmetic. For each candidate it combines:

1. **Historical quantity fit** — how strongly burst-adjusted transactions cluster around that stack size.
2. **Normalized convenience premium / bulk discount** — each historical sale price is divided by a nearby-in-time market baseline before comparing quantities, so changing market regimes do not create fake quantity effects.
3. **Buyer affordability** — candidate stack size × candidate price is compared against the historical distribution of complete transaction values.
4. **Sell-through fit** — stacks that are huge relative to current units/day are penalized.
5. **Fragmentation / manual-management cost** — many tiny listings are increasingly penalized, especially when each transaction is cheap; >20 implied listings receives an additional penalty.
6. **Evidence shrinkage** — when stack history is weak, the optimizer shrinks toward lower-fragmentation behavior rather than overfitting a few transactions.

Transactions falling inside the same two-minute purchase burst are discounted by `1 / sqrt(burst size)`. This is a heuristic to reduce the effect of one buyer sweeping multiple listings.

The normal recommendation maximizes the balanced utility. A second low-maintenance recommendation searches for a substantially lower listing count and only surfaces it when the utility loss is tolerable.

Price/rating statistics use the recent 30-day market regime. Stack-behavior analysis may use up to 90 days, with a 28-day recency half-life, because quantity-preference patterns need more observations than price-level estimation.

---

## v0.6 full live audit + vendor economics

### Full live audit

The native market refresh state machine now exposes a force-all-owned mode. The queue is built from all unique marketable item IDs represented in known ownership snapshots: player inventory, loaded/cached saddlebags, loaded/cached retainers, and current retainer-market snapshots. This mode intentionally ignores listing freshness and asks FFXIV for every item sequentially.

The audit keeps the RetainerSorter-style safety model: one active request, wait for matching market packets, settle, cooldown, timeout/retry, then advance. It is expected to take several minutes on hundreds of unique items; completeness and conservative request pacing are more important than speed.

### Vendor data

Vendor economics are intentionally self-contained and gil-only:

- `Item.PriceLow`: guaranteed NPC buyback value (NPC pays player).
- `GilShopItem`: confirms an item is actually sold by a normal gil vendor.
- `Item.PriceMid`: gil purchase cost (player pays NPC), only trusted when `GilShopItem` membership exists.
- SpecialShop/scrip/tomestone/other-currency exchanges are ignored.
- NQ gil-shop prices are not used as an HQ source comparison.

### Tax-normalized economics

Suggested listing price remains gross. The economic model derives a conservative net using a 5% seller-tax assumption:

`net = floor(gross × 0.95)`

Net is used for the absolute-value component, portfolio `Est. net`, NPC buyback comparison and vendor-to-market arbitrage comparison.

### NPC buyback floor

NPC buyback is an infinite-liquidity, immediate alternative with no retainer slot or waiting cost. Therefore it is not treated as just another weak feature:

- net MB <= NPC buyback: hard cap the sell-opportunity score into poor territory;
- net MB < 10% above buyback: very strong penalty;
- net MB < 25% above buyback: meaningful penalty;
- comfortably above buyback: floor becomes neutral rather than a reward.

### Vendor convenience arbitrage

For NQ items actually sold by a gil vendor, compare after-tax executable MB net to vendor purchase cost. Positive margin can increase the rating because players demonstrably pay for market-board convenience. The boost is bounded and requires recent sale/demand evidence. A high ask with no actual recent sales is not proof of arbitrage and receives no bonus.


## v0.8 UI / interaction contract

- Hovering any item/listing row highlights the full row.
- Clicking anywhere in a row opens the detail inspector; clicking it again or using Back closes it.
- Analytical table headers expose hover explanations.
- Right-clicking a Suggested price copies the raw gil number to the clipboard.
- Owned Items can be filtered by strict score range, star range, and expected-net recommended-listing range.
- Current Listings can be filtered by strict score range, star range, and expected current-listing payout range.
- The detail inspector includes the in-game item icon, recommendation, market evidence, vendor economics, score table, stack-analysis candidates, warnings and known locations.
- Current Listings explicitly exposes listed quantity, listed unit price, and expected after-tax payout for the exact listing.


### v0.8 listed-state + sale-unit semantics

- Owned item/HQ variants with a cached current retainer Market Board listing are gold-tinted in the overview.
- `Est. net` in Owned Items is the after-tax payout of **one recommended listing**, not the entire known stockpile.
- The full known-position value is retained in the detail inspector for context.
- The Value score uses the same recommended-listing payout, tying absolute value directly to the stack recommendation.
- More than roughly a dozen recommended listings introduces a mild logarithmic execution-friction penalty, capped so strong market evidence still dominates.
