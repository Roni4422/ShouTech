// shoutech_erp_v0.1.6/src/ShouTech.Domain/Entities/FYear.cs
using ShouTech.Domain.Entities;

namespace ShouTech.Domain.Entities;

public class FYear : EntBase
{
    public Guid CompanyId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public bool IsClosed { get; private set; }
    public string DatabaseName { get; private set; } = string.Empty;
    public bool IsCurrent { get; private set; }

    private FYear() { }

    public static FYear Create(
        Guid companyId,
        string name,
        DateOnly startDate,
        DateOnly endDate,
        string databaseName,
        Guid createdBy,
        bool isCurrent = false)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("CompanyId is required.", nameof(companyId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Fiscal year name is required.", nameof(name));
        if (endDate < startDate)
            throw new ArgumentException("EndDate cannot be earlier than StartDate.");
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("DatabaseName is required.", nameof(databaseName));

        return new FYear
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Name = name.Trim(),
            StartDate = startDate,
            EndDate = endDate,
            DatabaseName = databaseName.Trim(),
            IsCurrent = isCurrent,
            IsClosed = false,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };
    }

    public void Close(Guid closedBy)
    {
        if (IsClosed) return;
        IsClosed = true;
        IsCurrent = false;
        Touch(closedBy);
    }

    public void SetAsCurrent(Guid modifiedBy)
    {
        if (IsClosed)
            throw new InvalidOperationException("A closed fiscal year cannot be set as current.");
        IsCurrent = true;
        Touch(modifiedBy);
    }

    public void ClearCurrent(Guid modifiedBy)
    {
        if (!IsCurrent) return;
        IsCurrent = false;
        Touch(modifiedBy);
    }
}