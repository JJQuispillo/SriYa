namespace Qora.Billing.Domain.Entities;

/// <summary>
/// Representa una línea de detalle dentro de un documento de facturación.
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

    // Campos de sustento (codDocSustento del SRI)
    public string? SustentoDocumentType { get; private set; }        // máx. 2 caracteres
    public string? SustentoDocumentNumber { get; private set; }      // máx. 39 caracteres
    public DateTime? SustentoDocumentIssueDate { get; private set; }
    public string? SustentoDocumentAuthNumber { get; private set; }  // máx. 49 caracteres

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
            throw new ArgumentException("La cantidad debe ser mayor a cero.", nameof(quantity));
        if (unitPrice < 0)
            throw new ArgumentException("El precio unitario no puede ser negativo.", nameof(unitPrice));
        if (discount < 0)
            throw new ArgumentException("El descuento no puede ser negativo.", nameof(discount));

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
