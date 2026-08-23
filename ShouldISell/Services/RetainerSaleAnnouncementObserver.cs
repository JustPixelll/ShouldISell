using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace ShouldISell.Services;

/// <summary>
/// Passively records the RetainerSale chat notification while the player is online.
/// The notification itself gives us the item, sold stack quantity and after-fees gil.
/// Retainer/buyer attribution is reconciled later against the exact sale-history packet.
/// </summary>
public sealed class RetainerSaleAnnouncementObserver : IDisposable
{
    private readonly IChatGui chat;
    private readonly IPlayerState playerState;
    private readonly LocalStore store;
    private readonly IPluginLog log;

    public RetainerSaleAnnouncementObserver(
        IChatGui chat,
        IPlayerState playerState,
        LocalStore store,
        IPluginLog log)
    {
        this.chat = chat;
        this.playerState = playerState;
        this.store = store;
        this.log = log;
        chat.ChatMessage += OnChatMessage;
    }

    public void Dispose() => chat.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.RetainerSale ||
            !playerState.IsLoaded || playerState.ContentId == 0)
            return;

        try
        {
            Capture(message);
        }
        catch (Exception ex)
        {
            // A localized/changed notification must never interfere with the actual chat message.
            log.Warning(ex, "Could not parse a retainer-sale notification.");
        }
    }

    private void Capture(IHandleableChatMessage message)
    {
        var payloads = message.Message.Payloads;
        var itemIndex = payloads.FindIndex(x => x is ItemPayload);
        if (itemIndex < 0 || payloads[itemIndex] is not ItemPayload itemPayload || itemPayload.ItemId == 0)
        {
            log.Debug("RetainerSale message contained no item payload: {Message}", message.Message.TextValue);
            return;
        }

        var beforeItem = JoinText(payloads.Take(itemIndex));
        var afterItem = JoinText(payloads.Skip(itemIndex + 1));
        var beforeNumbers = ExtractPositiveIntegers(beforeItem);
        var afterNumbers = ExtractPositiveIntegers(afterItem);

        // Current FFXIV sale notifications put the sold stack count before the linked item and
        // the after-fees payout after it. A missing count means a single item.
        var quantity = beforeNumbers.Count == 0 ? 1 : ClampQuantity(beforeNumbers[^1]);
        var netGil = afterNumbers.Count == 0 ? 0 : afterNumbers[^1];
        if (netGil < 0)
            netGil = 0;

        var now = DateTimeOffset.UtcNow;
        var candidates = store.GetOwnListings(playerState.ContentId)
            .Where(x => x.ItemId == itemPayload.ItemId && x.IsHq == itemPayload.IsHQ)
            .ToList();

        if (quantity > 0)
        {
            var sameQuantity = candidates.Where(x => x.Quantity == quantity).ToList();
            if (sameQuantity.Count > 0)
                candidates = sameQuantity;
        }

        if (netGil > 0 && candidates.Count > 1)
        {
            // Notification payout and sale-history payout are both after fees. The cached listing
            // price is pre-fee, so use a deliberately broad seller-tax window only to disambiguate.
            var plausible = candidates.Where(x =>
            {
                var gross = (long)x.UnitPrice * Math.Max(1, x.Quantity);
                return gross >= netGil && netGil >= (long)Math.Floor(gross * 0.85);
            }).ToList();
            if (plausible.Count > 0)
                candidates = plausible;
        }

        // Only attribute a retainer when the cached listing evidence is unambiguous. Otherwise
        // preserve the exact item/qty/payout and let View sale history reconcile attribution later.
        var match = candidates.Count == 1 ? candidates[0] : null;
        var sale = new PersonalSale(
            playerState.ContentId,
            match?.RetainerId ?? 0,
            match?.RetainerName ?? "Unknown retainer",
            itemPayload.ItemId,
            quantity,
            itemPayload.IsHQ,
            netGil,
            now,
            string.Empty,
            now,
            PersonalSaleSource.Announcement,
            netGil == 0,
            false);

        if (store.AddPersonalSaleAnnouncement(sale))
        {
            store.Flush();
            log.Debug(
                "Captured live retainer sale notification for item {ItemId}, qty {Quantity}, net {NetGil}.",
                sale.ItemId, sale.Quantity, sale.NetGil);
        }
    }

    private static string JoinText(IEnumerable<Dalamud.Game.Text.SeStringHandling.Payload> payloads)
    {
        var builder = new StringBuilder();
        foreach (var payload in payloads)
        {
            if (payload is TextPayload text && !string.IsNullOrEmpty(text.Text))
                builder.Append(text.Text);
        }
        return builder.ToString();
    }

    private static int ClampQuantity(long value)
        => value is >= 1 and <= 999_999 ? (int)value : 1;

    private static List<long> ExtractPositiveIntegers(string text)
    {
        var values = new List<long>();
        var digits = new StringBuilder();

        void Flush()
        {
            if (digits.Length == 0)
                return;
            if (long.TryParse(digits.ToString(), out var value) && value > 0)
                values.Add(value);
            digits.Clear();
        }

        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
                continue;
            }

            // Thousands separators are safe to ignore while collecting one number. Letters and
            // other punctuation end the number. This handles 59,440 / 59.440 / 59 440 alike.
            if (digits.Length > 0 && (c is ',' or '.' or '\'' or '’' or '\u00A0' || char.IsWhiteSpace(c)))
                continue;

            Flush();
        }

        Flush();
        return values;
    }
}
