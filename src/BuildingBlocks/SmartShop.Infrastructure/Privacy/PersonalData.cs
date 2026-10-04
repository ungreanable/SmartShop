namespace SmartShop.Infrastructure.Privacy;

/// <summary>
/// PDPA data portability: every module that stores personal data contributes a section to the user's export.
/// </summary>
public interface IPersonalDataContributor
{
    string Section { get; }

    Task<object?> ExportAsync(Guid userId, CancellationToken ct = default);
}
