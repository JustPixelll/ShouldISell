# Should I Sell? v0.8.1 — Sales Ledger & Listing Attention

## New

- Added a **Sales History** tab for your own retainer sales.
- Opening a retainer's **View sale history** captures exact recent personal sale rows from the game: item/HQ, quantity, exact timestamp, buyer name and after-tax net gil.
- Captured sales are stored locally and deduplicated so the ledger grows as you revisit retainer sale histories.
- Sales are grouped by item with game icons and statistics including total net earned, transactions, units, net/unit, average sale, best sale and last sale.
- Added fun aggregate stats such as top earner, biggest transaction, best day and average transaction.

## Current Listings

- Current-listing rows now turn **amber** whenever action is recommended.
- A row is highlighted when the listed quantity differs from the recommended stack size, or the price recommendation is not **Keep**.
- The exact mismatching quantity/price/change cells are highlighted as well.

## Fixed

- Fixed the plugin icon/screenshots disappearing from the Dalamud plugin installer after installation by putting IconUrl/ImageUrls into the packaged plugin manifest metadata, not only the custom repository manifest.

## First use of Sales History

Visit a Summoning Bell and, for each retainer, open **View sale history** once. The game normally sends up to 20 recent history rows for that retainer. Should I Sell? stores those locally and will add new rows on future visits without duplicating old entries.

Requires Dalamud API 15.
