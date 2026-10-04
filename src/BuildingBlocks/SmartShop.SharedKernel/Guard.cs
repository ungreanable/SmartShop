namespace SmartShop.SharedKernel;

public static class Guard
{
    public static string NotEmpty(string? value, string field, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException("validation", $"{field} is required.");
        value = value.Trim();
        if (value.Length > maxLength)
            throw new DomainException("validation", $"{field} must be at most {maxLength} characters.");
        return value;
    }

    public static string? Optional(string? value, string field, int maxLength = 1000)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > maxLength)
            throw new DomainException("validation", $"{field} must be at most {maxLength} characters.");
        return value;
    }

    public static decimal Money(decimal value, string field)
    {
        if (value < 0 || value > 1_000_000)
            throw new DomainException("validation", $"{field} must be between 0 and 1,000,000.");
        return decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    public static int Range(int value, string field, int min, int max)
    {
        if (value < min || value > max)
            throw new DomainException("validation", $"{field} must be between {min} and {max}.");
        return value;
    }

    public static void That(bool condition, string code, string message)
    {
        if (!condition) throw new DomainException(code, message);
    }
}
