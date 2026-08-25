# Should I? architecture and decision logic

This is the reviewer-oriented map of the current plugin: what each service owns, which evidence each model uses, and where uncertainty is deliberately preserved.

## Product boundary

Should I? provides economic decision support for FFXIV. It never buys, sells, reprices, clicks listings, or queues native Market Board searches. Network work initiated by Should I? is limited to public Universalis HTTP endpoints. Native FFXIV market data is consumed only when ordinary game use or an optional compatible local provider has already exposed it.

Every recommendation is an estimate at a point in time. Rating and confidence are separate so an attractive but weakly evidenced action remains visibly uncertain.

## Runtime composition

`Plugin` is the composition root. Its services fall into four groups:

1. **Observation and persistence:** `InventoryScanner`, `MarketBoardObserver`, the two retainer sale observers, `MarketPurchaseObserver`, `LocalStore`, `TraderStore`, and `ListingHistoryTracker`.
2. **Market acquisition:** `UniversalisClient`, `BuyOpportunityScanner`, `ProductionOpportunityScanner`, and the passive `ExternalMarketDataBridge`.
3. **Decision models:** `ScoreCalculator`, `MarketDataCoordinator`, `TraderAnalyzer`, and `TycoonInsightService`.
4. **Presentation:** `SuiteWindow`, the Sell `MainWindow`, and the additive tooltip/context-menu/retainer overlay integrations.

## Evidence and freshness

### Static game data

Lumina sheets provide item metadata, marketability, recipes, job requirements, gathering sources and normal-gil vendor membership. `GameItemCatalog` verifies vendor availability through `GilShopItem`; `Item.PriceMid` alone is not proof that an NPC sells an item.

### Inventory and listings

FFXIV does not keep every retainer container loaded. The plugin snapshots only containers the game has loaded and keeps earlier snapshots for unloaded retainers. The UI warns users that opening relevant containers improves coverage.

### Current market data

Source priority is:

1. a newer live FFXIV observation;
2. an optional compatible local snapshot;
3. Universalis.

Cache freshness means when Should I? fetched or observed a snapshot. Universalis' upstream `lastUploadTime` is stored separately so evidence age is not confused with HTTP-fetch time.

### Sale history

Universalis history requests ask for up to 1,800 entries from the trailing 90 days. The API uses seconds for `entriesWithin` and milliseconds for `statsWithin`; both clients keep those units explicit.

Listing freshness and sale recency are independent. A board observed now with no sale for weeks is fresh evidence of low demand.

## Sell model

`ScoreCalculator` filters by item quality, keeps a 90-day history and emphasizes the latest 30 days. It derives an executable price, sold-price anchors and quartiles, units/day and transactions/day, days of supply, approximate queue time, stability, trend, jointly optimized stack/price guidance, and vendor economics.

| Component | Weight |
|---|---:|
| Price attractiveness | 25% |
| Demand | 17% |
| Supply | 12% |
| Liquidity | 11% |
| Stability | 9% |
| Trend | 5% |
| One recommended listing's after-tax value | 11% |
| Vendor economics | 10% |

Stars use a contrast-expanded presentation scale; the numeric 0–100 value keeps the stricter weighted signal. Confidence depends on sample size, listing freshness and last-sale age and does not inflate the underlying score.

The meaningful-listing-value setting is a smooth reference, not a hard filter. It applies to one recommended listing, not one unit or the entire stockpile.

## Buy model

Buy discovery has separate Market Board and normal-gil Vendor lanes. The broad pass uses current-world Universalis aggregate evidence. A bounded detailed pass retrieves listing identities, reported buyer tax and 90-day history. Market-to-market candidates are evaluated counterfactually: acquired listings are removed before the shared Sell model estimates the resulting exit.

Supported routes are Market to Market, split/consolidate based on observed stack behavior, renewable Vendor to Market, and guaranteed Market to Vendor when acquisition cost including buyer tax is below NPC buyback.

Discovery has no hidden user-preference budget, minimum-profit, minimum-ROI or holding-time gate. Those are explicit findings filters in the UI. Only economically positive, executable packages enter results. Liquidity affects ranking through a fixed 14-day scoring horizon, not silent removal. A 999,999,999g package ceiling reflects the game-scale gil boundary rather than a personal budget.

Vendor supply is never treated as scarce. Vendor recommendations are constrained to working inventory, current ownership and a profit-dependent maximum holding window. The displayed break-even buy price is the pre-tax acquisition ceiling at the modeled after-tax exit; it is not a target.

## Craft model

Craft considers recipes the current character can perform and currently compares NQ output with NQ inputs. Recursive ingredient resolution chooses the cheapest available route among Market Board ask plus conservative 5% buyer tax, verified normal-gil vendor, and another craft recipe up to a bounded depth.

Owned direct ingredients reduce cash required but retain economic opportunity cost. Result proceeds include conservative seller tax. The model reports cash and economic profit, then scores ROI, profit, demand, liquidation, stability and confidence. Generic craft duration is explicitly lower-confidence.

Only the strongest rough result markets receive detailed 90-day validation. Final rows are validated shortlist results, not a claim that every recipe received a full history request.

## Gather model

Gather currently covers accessible Miner and Botanist sources. Fishing is excluded until its weather/time/route requirements can be modeled defensibly.

The model combines source availability, job levels, current sale value, demand, volatility and one generic active-yield baseline. It does not manufacture a percentage range. Gear, route execution and node-topology uncertainty stay in confidence.

Displayed value per active minute is after-tax market value created under the baseline. It is not guaranteed realized gil and does not charge timed-node waiting as active play time.

## Should I Do?

Should I Do? merges cached Buy, Craft and Gather results for the current world. It does not start hidden native work. Craft rows can become Craft + Gather when an ingredient has a strong gather result, but gathered inputs are never economically free.

## Tycoon and FIFO

Market Board purchases are recorded only after request and successful acknowledgement match. Vendor purchases require explicit confirmation. FIFO is evaluated per item/HQ variant and consumes opening inventory before tracked purchase lots. This prevents pre-existing, gathered or crafted stock from fabricating realized trading profit.

Tycoon separates matched trade P&L, open tracked lots, all captured personal sales including unknown-cost stock, direct wallet changes whose source may remain unknown, and listing lifecycles with only conservative sale correlation.

Render-facing analytics use revision-based invalidation. Meaningful inventory, market, sale, purchase or listing-lifecycle changes invalidate the relevant snapshot; unchanged frames reuse it.

## Persistence and concurrency

JSON documents live in Dalamud's plugin configuration directory and use temporary-file replacement. Store mutations are locked, readers receive copied collections/snapshots, and failed persistence leaves the document dirty for retry.

The shared Sell rating cache is locked because UI and optional IPC callers can overlap. Network refreshes are serialized by a non-blocking semaphore so repeated clicks do not create concurrent duplicate refreshes.

## Degraded behavior

- A retainer-history signature miss disables exact historical capture but not the plugin.
- A tooltip quantity-hook miss leaves cached tooltip insight available without current stack value.
- A vendor-sheet schema failure disables vendor-purchase comparisons and is logged.
- Missing evidence produces low confidence or no recommendation rather than invented values.

## Review invariants

- No automatic transaction or native queued Market Board search.
- Current-world results never survive a world change.
- Owned materials are not economically free.
- Acquisition tax and seller tax remain on their correct sides of a trade.
- Cost basis stays unknown unless observed or explicitly entered.
- Confidence remains separate from opportunity score.
- Stale upstream timestamps are not presented as fresh observation.
- User preference filters are visible in the UI rather than hidden in discovery.
