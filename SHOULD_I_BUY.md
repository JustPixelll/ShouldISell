# Should I Buy? — first integrated implementation

Should I Buy? is the acquisition side of the same market engine used by Should I Sell?. It is intentionally packaged in the ShouldISell plugin so acquisition, inventory, listing guidance and realized sales can eventually form one closed trading loop.

## Open it

```text
/buycheck
```

Start a scan directly:

```text
/buycheck scan
```

Stop a running scan:

```text
/buycheck stop
```

## Discovery pipeline

1. Fetch the Universalis marketable-item universe.
2. Query the Universalis aggregated endpoint in batches of up to 100 item IDs. This is the cheap discovery pass and uses minimum listing price, recent average sale price and daily sale velocity.
3. Keep only the strongest rough candidates according to the configured strategy/risk thresholds.
4. Fetch current listing depth and up to 90 days of sale history for the deep-candidate set.
5. Reuse Should I Sell?'s existing sale-price, stack-size, demand, liquidity, stability and confidence model to simulate the exit.
6. Rank concrete acquisition packages rather than abstract items.

## Strategies

### Market → Market

The engine walks the cheapest listing prefixes because a Market Board listing is indivisible. For each package it removes the listings from a counterfactual `MarketSnapshot`, adds the acquired quantity to the player's existing position, and asks the Should I Sell? model how that resulting position should be exited.

The best package can therefore be "buy the first three listings" rather than "buy everything below some historical median".

### Buy & split / consolidate

Market → Market packages are relabeled when the shared historical stack optimizer strongly prefers an exit stack materially smaller or larger than the acquired listing sizes.

### Vendor → Market

Normal gil-vendor prices come from real `GilShopItem` membership. Recommended quantity is capped by observed daily velocity, the configured holding horizon, stack limit and per-item budget exposure. A huge nominal markup with no recent demand is not treated as an excuse to buy 99.

### Market → Vendor

NQ listings whose estimated total acquisition cost remains below guaranteed NPC buyback are grouped into a guaranteed-exit package. This path deliberately does not assume an HQ vendor multiplier.

## Rating

Stars are the broad recommendation band. The numeric 0–100 score is stricter. Confidence remains separate.

The buy score combines:

- risk-adjusted ROI — 22%
- absolute potential profit — 20%
- liquidity / modeled exit time — 18%
- price advantage — 12%
- demand — 10%
- stability — 7%
- evidence confidence — 6%
- execution friction — 5%

`Potential profit` assumes the modeled exit succeeds. `Risk-adjusted profit` discounts that potential for evidence confidence and estimated capital lock-up; it is a ranking heuristic, not a guarantee.

## Portfolio

The Portfolio tab allocates the configured budget across non-overlapping item/HQ packages. It may leave gil in reserve when the remaining packages are weaker or do not fit the remaining budget.

## Personal purchase ledger

Successful **Market Board** purchases are passively recorded through Dalamud's normal Market Board events. The ledger stores:

- item/HQ
- quantity
- unit price
- actual purchase tax
- total cost basis
- listing/retainer identifiers
- world and timestamp
- matched recommendation strategy when available
- predicted exit price, ROI, profit and liquidation time when the purchase matches a recent recommendation

No automatic purchasing is performed.

## Trader Profile

The Trader Profile joins recorded purchases with Should I Sell?'s existing personal retainer-sale ledger.

FIFO matching is deliberately conservative: only sold units that can be connected to an earlier recorded buy are counted as trading P&L. Crafted items, old inventory and otherwise unmatched sales are excluded from trading ROI rather than assigned an invented cost basis.

The current profile reports:

- capital deployed
- matched realized profit and ROI
- win rate
- average holding time
- open trading cost basis
- sale coverage
- strategy-level ROI/profit/holding time
- open positions by cost basis
- prediction error for sell time and exit price once enough closed trades exist

## Scope and settings

The first UI exposes budget, minimum profit, minimum ROI, target holding period, maximum capital exposure per item, deep-analysis candidate count, estimated discovery-time buyer tax, strategy toggles and HQ inclusion.

The catalog also exposes FFXIV `ItemSearchCategory` metadata for category-scoped discovery; category UI/scanner wiring is intentionally kept as a small follow-up if the first in-game build shows the Lumina category labels need grouping for usability.

## Important limitations of this first integrated build

- Universalis aggregate velocity is a short-window discovery signal; deep candidates still use the longer Should I Sell? history model.
- Discovery-time buyer tax is configurable because unseen listings do not provide the actual tax to this client. Completed Market Board purchases store the actual tax reported by Dalamud.
- Vendor purchases are not yet automatically captured into cost basis because they do not pass through Dalamud's Market Board purchase events.
- Cross-world arbitrage is designed for but not enabled in this first implementation.
- The portfolio allocator is greedy rather than a full integer-programming optimizer.
- Recommendations are analysis, not guarantees, and all purchases remain manual.
