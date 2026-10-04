namespace SmartShop.SharedKernel;

/// <summary>A business rule was violated. Mapped to HTTP 400 with a machine-readable code.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The current state does not allow the operation (e.g. order already accepted). HTTP 409.</summary>
public sealed class ConflictException(string code, string message) : DomainException(code, message);

public sealed class NotFoundException(string resource, object id)
    : Exception($"{resource} '{id}' was not found.")
{
    public string Resource { get; } = resource;
}

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.")
    : Exception(message);
