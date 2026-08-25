# Should I? v2.3.5

## Reviewer and usability cleanup

- Renames the unified action view from **Opportunities** to **Should I Do?** while keeping `/shouldi opportunities` as a compatibility alias.
- Gives Craft, Gather and Should I Do? result tables the remaining window height. Craft and Gather details now open as focused pages with a clear back action.
- Keeps click-to-sort headers across every row-oriented data table.
- Removes historical implementation-version labels and stale development documentation from current UI explanations.

## Logic corrections

- Fixes Production's Universalis history request parameter name and its `statsWithin` time unit.
- Measures production-market velocity through analysis time so an old, short sales burst cannot appear continuously active.
- Includes a conservative 5% buyer tax when Market Board ingredients compete with vendor/craft routes.
- Describes Gather's generic rate as after-tax market value created per active minute, not guaranteed gil income.
- Removes legacy hidden Buy profit, ROI and holding-time gates. Those remain explicit findings filters; liquidity affects ranking through a documented 14-day scoring horizon.
- Replaces dummy discovery budget fields with an explicit game-scale package guard and a clearly named break-even buy-price calculation.

## Reliability and maintainability

- Replaces Tycoon's two-second render cache with revision-based invalidation tied to sales and listing lifecycle changes.
- Makes the shared sell-rating cache safe for concurrent IPC/UI callers and unsubscribes its Universalis handlers during disposal.
- Logs optional vendor-sheet and tooltip-hook failures instead of swallowing them.
- Refreshes architecture and development documentation around current official-safe data flow and degraded behavior.
