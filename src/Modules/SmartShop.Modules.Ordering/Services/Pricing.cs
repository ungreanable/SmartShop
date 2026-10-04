using SmartShop.Contracts.Catalog;
using SmartShop.Modules.Ordering.Domain;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Ordering.Services;

public sealed record PricedLine(decimal UnitPrice, List<OrderLineOption> Options, string? Error);

/// <summary>Validates selected options against the item's option groups and prices a line.</summary>
internal static class LinePricer
{
    public static PricedLine Price(CheckoutItem item, IReadOnlyCollection<Guid> optionIds)
    {
        var options = new List<OrderLineOption>();
        var known = item.ModifierGroups.SelectMany(g => g.Options.Select(o => (Group: g, Option: o))).ToDictionary(x => x.Option.Id);
        foreach (var id in optionIds)
        {
            if (!known.TryGetValue(id, out var pair)) return new(item.Price, options, "option_unknown");
            if (!pair.Option.IsAvailable) return new(item.Price, options, "option_unavailable");
        }
        foreach (var group in item.ModifierGroups)
        {
            var selected = group.Options.Where(o => optionIds.Contains(o.Id)).ToList();
            if (selected.Count < group.MinSelect || selected.Count > group.MaxSelect)
                return new(item.Price, options, $"option_group:{group.Name}");
            options.AddRange(selected.Select(o => new OrderLineOption { Group = group.Name, Name = o.Name, PriceDelta = o.PriceDelta }));
        }
        return new(item.Price, options, null);
    }

    public static string ErrorMessage(string itemName, string error) => error switch
    {
        "option_unknown" => $"\"{itemName}\": an option no longer exists. Please choose again.",
        "option_unavailable" => $"\"{itemName}\": a selected option is unavailable.",
        _ when error.StartsWith("option_group:", StringComparison.Ordinal) => $"\"{itemName}\": please check the choices for \"{error[13..]}\".",
        _ => $"\"{itemName}\" cannot be ordered ({error}).",
    };

    public static void ThrowIfUnavailable(CheckoutItem item, int quantity)
    {
        if (!item.IsOrderable)
            throw new DomainException("item_unavailable", item.UnavailableReason switch
            {
                "sold_out" => $"\"{item.Name}\" is sold out.",
                "outside_hours" => $"\"{item.Name}\" is not sold at the selected time.",
                "round_closed" => $"Pre-orders for \"{item.Name}\" are closed.",
                _ => $"\"{item.Name}\" is not available.",
            });
        if (item.MaxPerOrder is { } max && quantity > max)
            throw new DomainException("max_per_order", $"\"{item.Name}\" is limited to {max} per order.");
        if (item.Available is { } available && quantity > available)
            throw new DomainException("out_of_stock", $"Only {available} \"{item.Name}\" left.");
    }
}
