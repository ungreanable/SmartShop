using SmartShop.Contracts.Shops;
using SmartShop.SharedKernel;

namespace SmartShop.Modules.Shops.Domain;

public sealed record PaymentMethodDetails(
    string? PromptPayId = null, Guid? QrImageId = null,
    string? BankName = null, string? AccountNumber = null, string? AccountName = null,
    string? Instructions = null, Guid? ImageId = null);

/// <summary>How customers can pay this shop. Configured by the shop owner/manager.</summary>
public sealed class PaymentMethod
{
    private PaymentMethod() { }

    public Guid Id { get; private set; }
    public Guid ShopId { get; private set; }
    public PaymentMethodType Type { get; private set; }
    public string DisplayName { get; private set; } = "";
    public string? PromptPayId { get; private set; }
    public Guid? QrImageId { get; private set; }
    public string? BankName { get; private set; }
    public string? AccountNumber { get; private set; }
    public string? AccountName { get; private set; }
    public string? Instructions { get; private set; }
    public Guid? ImageId { get; private set; }
    public bool RequiresProof { get; private set; }
    public bool Enabled { get; private set; } = true;
    public int SortOrder { get; private set; }

    public static PaymentMethod Create(Guid shopId, PaymentMethodType type, string displayName, PaymentMethodDetails details, int sortOrder)
    {
        var method = new PaymentMethod { Id = Ids.New(), ShopId = shopId, Type = type, SortOrder = sortOrder };
        method.Update(displayName, details, null, true);
        return method;
    }

    public void Update(string displayName, PaymentMethodDetails d, bool? requiresProof, bool enabled)
    {
        DisplayName = Guard.NotEmpty(displayName, "Display name", 60);
        Instructions = Guard.Optional(d.Instructions, "Instructions", 500);
        ImageId = d.ImageId;
        Enabled = enabled;

        PromptPayId = null; QrImageId = null; BankName = null; AccountNumber = null; AccountName = null;
        switch (Type)
        {
            case PaymentMethodType.Cash:
                RequiresProof = false;
                break;
            case PaymentMethodType.PromptPayQr:
                PromptPayId = NormalizePromptPay(d.PromptPayId);
                QrImageId = d.QrImageId;
                if (PromptPayId is null && QrImageId is null)
                    throw new DomainException("validation", "PromptPay needs a PromptPay ID (phone or citizen/tax ID) or a QR image.");
                RequiresProof = requiresProof ?? true;
                break;
            case PaymentMethodType.BankTransfer:
                BankName = Guard.NotEmpty(d.BankName, "Bank", 60);
                AccountNumber = Guard.NotEmpty(d.AccountNumber, "Account number", 30);
                AccountName = Guard.NotEmpty(d.AccountName, "Account name", 100);
                RequiresProof = requiresProof ?? true;
                break;
            case PaymentMethodType.Custom:
                RequiresProof = requiresProof ?? false;
                break;
        }
    }

    public void SetOrder(int sortOrder) => SortOrder = sortOrder;

    /// <summary>PromptPay accepts a 10-digit mobile number, 13-digit citizen/tax id or 15-digit e-wallet id.</summary>
    private static string? NormalizePromptPay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length is not (10 or 13 or 15))
            throw new DomainException("validation", "PromptPay ID must be a 10-digit phone number, 13-digit ID or 15-digit e-wallet ID.");
        return digits;
    }

    public PaymentMethodInfo ToInfo() => new(Id, Type, DisplayName, PromptPayId, QrImageId, BankName, AccountNumber, AccountName, Instructions, ImageId, RequiresProof);
}
