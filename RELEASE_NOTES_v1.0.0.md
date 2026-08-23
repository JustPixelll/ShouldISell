# Should I Sell? v1.0.0 — Passive Sales & Retainer Alerts

This is the first 1.0 release of Should I Sell?. It turns the personal sales ledger into an always-on feature and makes active listing maintenance substantially faster.

## Automatic Sales History

- Retainer-sale announcements are captured automatically while you are online.
- The live notification supplies the linked item, sold quantity and after-fees gil, so new sales can be persisted immediately without opening a retainer.
- Cached listing evidence is used to attribute the retainer only when that attribution is unambiguous.
- Opening **View sale history** still matters as an exact reconciliation/backfill path:
  - offline sales can be added,
  - live rows can be matched to the exact retainer,
  - buyer names and exact server sale timestamps are filled in,
  - duplicate live/history observations are merged instead of double-counted.
- Sales History now shows whether a transaction is **Live**, **History**, or **Live + confirmed**.

## Current Listings

- Added a dedicated **Refresh current listings** button.
- It requests fresh in-game Market Board data only for the unique items you currently have listed, instead of auditing every owned item.
- Added `/sellcheck listings` for the same action.

## Repricing Stability Fix

- Fixed a self-undercut feedback bug where a listing could bounce between recommendations such as **81g → 79g → 81g** after each repricing.
- Recommendation calculations now exclude all market-depth rows belonging to the player's known retainers for that item/HQ variant by retainer identity, rather than removing only an exact current price/quantity match.
- This keeps stale copies of your own previous price from being treated as a competitor after a repricing change.

## Retainer Market Attention

- When FFXIV's **RetainerSellList** is open, Should I Sell? displays a small companion panel beside it.
- Listings that need action receive an amber **!** and show the recommended price/stack change.
- A `KEEP` price does not trigger a price alert; a stack mismatch still does.
- The overlay is read-only and does not rewrite or click native FFXIV UI nodes.

## Notes

- Passive sale capture only sees sale notifications delivered while FFXIV/Should I Sell? is running. Exact retainer history remains the backfill for sales that happened while offline.
- FFXIV normally exposes only a limited number of recent exact sale-history rows per retainer, so checking history occasionally is still useful.
- Requires Dalamud API 15.
