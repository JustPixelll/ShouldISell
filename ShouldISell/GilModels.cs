namespace ShouldISell;

/// <summary>
/// User-facing classification for direct changes to the player's gil wallet. Only Market Board
/// purchases are automatically classified when Should I? can correlate the balance delta with an
/// exact confirmed purchase. Other categories are deliberately user-editable instead of guessed.
/// </summary>
public enum GilFlowCategory
{
    Unclassified,
    MarketBoardPurchase,
    Vendor,
    Quest,
    Duty,
    Teleport,
    Repair,
    Crafting,
    Glamour,
    Housing,
    PlayerTrade,
    RetainerTransfer,
    Other,
}

/// <summary>
/// One directly observed change to the logged-in character's gil balance. Amount is positive for
/// income and negative for spending. Market Board sales are intentionally kept in Should I Sell?'s
/// sale ledger because retainer revenue is earned before it is withdrawn into the player wallet.
/// </summary>
public sealed record GilFlowEntry(
    string Id,
    ulong CharacterContentId,
    long Amount,
    long BalanceAfter,
    DateTimeOffset AtUtc,
    GilFlowCategory Category,
    string Source,
    bool AutoClassified,
    string Note);
