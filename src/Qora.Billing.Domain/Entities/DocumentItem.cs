namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Represents a line item within a billing document.
/// </summary>
public class DocumentItem
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid DocumentId { get; private set; }
    public string MainCode { get; private set; } = string.Empty;
    public string? AuxiliaryCode { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Discount { get; private set; }
    public decimal Subtotal => (Quantity * UnitPrice) - Discount;
    public decimal TaxRate { get; private set; }
    public string TaxCode { get; private set; } = string.Empty;
    public string TaxPercentageCode { get; private set; } = string.Empty;

    // Sustento fields (SRI codDocSustento)
    public string? SustentoDocumentType { get; private set; }        // max 2 chars
    public string? SustentoDocumentNumber { get; private set; }      // max 39 chars
    public DateTime? SustentoDocumentIssueDate { get; private set; }
    public string? SustentoDocumentAuthNumber { get; private set; }  // max 49 chars

    private DocumentItem() { } // EF Core

    public static DocumentItem Create(
        Guid documentId,
        string mainCode,
        string description,
        decimal quantity,
        decimal unitPrice,
        decimal discount,
        decimal taxRate,
        string taxCode,
        string taxPercentageCode,
        string? auxiliaryCode = null,
        string? sustentoDocumentType = null,
        string? sustentoDocumentNumber = null,
        DateTime? sustentoDocumentIssueDate = null,
        string? sustentoDocumentAuthNumber = null)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be greater than zero.", nameof(quantity));
        if (unitPrice < 0)
            throw new ArgumentException("Unit price cannot be negative.", nameof(unitPrice));
        if (discount < 0)
            throw new ArgumentException("Discount cannot be negative.", nameof(discount));

        return new DocumentItem
        {
            DocumentId = documentId,
            MainCode = mainCode ?? throw new ArgumentNullException(nameof(mainCode)),
            Description = description ?? throw new ArgumentNullException(nameof(description)),
            Quantity = quantity,
            UnitPrice = unitPrice,
            Discount = discount,
            TaxRate = taxRate,
            TaxCode = taxCode ?? throw new ArgumentNullException(nameof(taxCode)),
            TaxPercentageCode = taxPercentageCode ?? throw new ArgumentNullException(nameof(taxPercentageCode)),
            AuxiliaryCode = auxiliaryCode,
            SustentoDocumentType = sustentoDocumentType,
            SustentoDocumentNumber = sustentoDocumentNumber,
            SustentoDocumentIssueDate = sustentoDocumentIssueDate,
            SustentoDocumentAuthNumber = sustentoDocumentAuthNumber
        };
    }
}
