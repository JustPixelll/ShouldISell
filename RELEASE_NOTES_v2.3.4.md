# Should I? v2.3.4

## Review fixes

- Prevents pre-existing inventory with unknown cost basis from being matched to newly tracked FIFO purchase lots, keeping realized profit conservative.
- Replaces Tycoon's time-based render-loop recomputation with revision-based invalidation tied to meaningful inventory, market, sale, purchase and configuration changes.
- Continues scanning later Market Board lots when one profitable vendor-arbitrage package is too large for the configured budget.
- Keeps Deep Mine smart candidates on the current world and reports analysis freshness for Craft/Gather candidates.

## Sortable tables

- Adds click-to-sort headers to every row-oriented data table across Sell, Buy, Craft, Gather, Opportunities and Tycoon.
- Preserves useful default ordering, such as rating/profit descending and newest transactions first.
