# Should I Sell? v0.8.0 — Initial Public Release

Should I Sell? is an experimental FFXIV/Dalamud Market Board analysis plugin that helps answer **what to sell, what price to use, and how many items to put in each listing**.

## Highlights

- 1–5 star sell-opportunity rating plus a stricter 0–100 score
- Suggested executable Market Board price
- Suggested stack size with low-maintenance alternative
- Expected after-tax payout for one recommended listing
- Full-stock value retained in the detailed item inspector
- Already-listed items highlighted in gold in the Owned Items overview
- Current Listings audit with listed quantity, expected payout and repricing guidance
- Full live audit of every known marketable owned item
- Universalis history integration
- Supply, demand, liquidity, price stability and trend analysis
- Historical stack-size / buyer-spend analysis
- NPC buyback and normal gil-vendor economics
- Persistent retainer/saddlebag ownership snapshots
- Sortable/filterable tables with rich item details
- Right-click Suggested price to copy the raw gil value

## First-install setup — important

FFXIV does not keep every inventory container loaded all the time. After installing, open your:

- normal inventory,
- Chocobo Saddlebag,
- Premium Saddlebags if applicable,
- every retainer inventory at a Summoning Bell,
- and each retainer's Market Board/sell interface if it has active listings.

Give each container a moment to load so Should I Sell? can snapshot it.

Then run:

```text
/sellcheck fetch
/sellcheck audit
```

`/sellcheck fetch` loads deeper Universalis history for the items the plugin now knows you own. `/sellcheck audit` performs a fresh in-game Market Board audit of every known marketable owned item.

A full audit is intentionally paced one item at a time and can take 10–20+ minutes for a few hundred unique items.

## Rating/value behavior

The **Meaningful listing value** setting refers to the after-tax payout of **one recommended listing**, not the value of the entire stockpile. This prevents a 105-item position that should be sold one-at-a-time from looking like one 105-item transaction.

Recommendations that imply very large numbers of separate listings receive a mild execution-friction penalty.

## Dalamud

Requires Dalamud API 15.
