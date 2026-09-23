namespace Assessment.Api.Domain;

internal static class Guard
{
    public const int DescriptionMaxLength = 2000;
    public const int ReasonMaxLength = 1000;
    public const decimal MaxAmount = 10_000_000m;

    public static string Description(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new DomainException("Description is required.");
        if (trimmed.Length > DescriptionMaxLength)
            throw new DomainException($"Description must be at most {DescriptionMaxLength} characters.");
        return trimmed;
    }

    public static decimal Amount(decimal value, string name)
    {
        if (value <= 0) throw new DomainException($"{name} must be greater than zero.");
        if (value > MaxAmount) throw new DomainException($"{name} must be at most {MaxAmount:N0}.");
        if (decimal.Round(value, 2) != value) throw new DomainException($"{name} can have at most 2 decimal places.");
        return value;
    }

    public static string Reason(string? value, string name = "Reason")
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new DomainException($"{name} is required.");
        if (trimmed.Length > ReasonMaxLength)
            throw new DomainException($"{name} must be at most {ReasonMaxLength} characters.");
        return trimmed;
    }

    public static string? OptionalText(string? value, string name)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > ReasonMaxLength
            ? throw new DomainException($"{name} must be at most {ReasonMaxLength} characters.")
            : trimmed;
    }
}
