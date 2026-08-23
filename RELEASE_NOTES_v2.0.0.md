# Should I? v2.0.0

Should I? has grown from a selling helper into an FFXIV **economy intelligence suite**. v2 formalizes that identity and fixes an important Buy discovery regression.

## Should I Buy? — scanner breadth restored

- Fixes a v1.1.7 shortlist regression where discovery-only DC/region/listing signals could crowd strong Phoenix/current-world candidates out of the limited 90-day deep-analysis pool.
- Current-world recent-sale candidates again form the primary deep-analysis pool.
- Rare/high-ticket items still get a bounded rescue lane instead of disappearing when the local four-day aggregate is empty.
- High-gil opportunities get a separate local, sale-backed discovery lane.
- Discovery references are now hierarchical and conservative; an inflated remote/listing median can no longer override good local sale evidence simply because it is numerically higher.
- Final recommendations are still based on detailed current-world listings + 90-day history and still obey budget, per-item exposure, minimum profit/ROI, holding-time and vendor-supply safeguards.
- Scanner status now exposes broad-signal and local/rare deep-candidate counts so a suspiciously narrow result set is diagnosable.

## Should I Sell? — sortable listing state age

- The Current Listings **As-is** column is now sortable.
- It continues to represent the age of the exact current price + quantity state, with hover detail for full observed lifetime, price age and quantity age.

## Should I Tycoon? — v2 economy layer

- Full observed player-wallet cashflow ledger.
- Editable income/spend categories with explicit Unclassified handling when source identity cannot be proven.
- Exact Market Board purchase attribution where wallet deltas match confirmed purchases.
- Trade Positions separate from personal/crafting/glamour purchases.
- FIFO cost basis, realized/unrealized P&L, strategy/item performance and model calibration.
- Sales Insights and Listing Insights remain linked to Should I Sell?.

## Project identity

- Public-facing workflow/release terminology now consistently says **Should I?** rather than the old Sell-only name.
- New suite icon and stronger public description.
- `InternalName = ShouldISell` and the technical project/repository path are intentionally retained for upgrade compatibility.
