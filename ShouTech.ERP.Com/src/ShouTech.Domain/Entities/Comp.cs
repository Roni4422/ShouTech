// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/Comp.cs
using ShouTech.Domain.Entities;


public class Comp : EntBase
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string NameSecondary { get; private set; } = string.Empty;
    public string? TaxNumber { get; private set; }
    public string DefaultCurrencyCode { get; private set; } = "SAR";
    public bool IsActive { get; private set; } = true;
    public Guid? GroupId { get; private set; }
    public int SortOrder { get; private set; }
    public string? Notes { get; private set; }

    private Comp() { }

    public static Comp Create(
        string code,
        string name,
        string nameSecondary,
        string defaultCurrencyCode,
        Guid createdBy,
        string? taxNumber = null,
        Guid? groupId = null,
        int sortOrder = 0,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Company code is required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Company name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(defaultCurrencyCode) || defaultCurrencyCode.Length != 3)
            throw new ArgumentException("Default currency must be a valid 3-letter ISO code.", nameof(defaultCurrencyCode));

        return new Comp
        {
            Id = Guid.NewGuid(),
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            NameSecondary = nameSecondary?.Trim() ?? string.Empty,
            DefaultCurrencyCode = defaultCurrencyCode.Trim().ToUpperInvariant(),
            TaxNumber = taxNumber?.Trim(),
            GroupId = groupId,
            SortOrder = sortOrder,
            Notes = notes?.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    public void Update(
        string name,
        string nameSecondary,
        string? taxNumber,
        string defaultCurrencyCode,
        Guid? groupId,
        int sortOrder,
        string? notes,
        Guid modifiedBy)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Company name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(defaultCurrencyCode) || defaultCurrencyCode.Length != 3)
            throw new ArgumentException("Default currency must be a valid 3-letter ISO code.", nameof(defaultCurrencyCode));

        Name = name.Trim();
        NameSecondary = nameSecondary?.Trim() ?? string.Empty;
        TaxNumber = taxNumber?.Trim();
        DefaultCurrencyCode = defaultCurrencyCode.Trim().ToUpperInvariant();
        GroupId = groupId;
        SortOrder = sortOrder;
        Notes = notes?.Trim();
        Touch(modifiedBy);
    }

    public void SetActive(bool isActive, Guid modifiedBy)
    {
        if (IsActive == isActive) return;
        IsActive = isActive;
        Touch(modifiedBy);
    }
}