using Qora.Billing.Domain.Enums;

namespace Qora.Billing.Application.DTOs;

public record CreateDocumentRequest(
    DocumentType DocumentType,
    Dictionary<string, string> IssuerInfo,
    Dictionary<string, string> BuyerInfo,
    List<DocumentItemDto> Items,
    Dictionary<string, string>? AdditionalInfo = null,
    List<DestinatarioDto>? Destinatarios = null);

public record DocumentItemDto(
    string MainCode,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    /// <summary>
    /// Ignorado — el command handler deriva el TaxRate de la tabla de referencia de códigos de impuestos del SRI.
    /// Se mantiene por compatibilidad hacia atrás; cualquier valor proporcionado se descarta.
    /// </summary>
    decimal TaxRate,
    string TaxCode,
    string TaxPercentageCode,
    string? AuxiliaryCode = null,
    string? SustentoDocumentType = null,
    string? SustentoDocumentNumber = null,
    DateTime? SustentoDocumentIssueDate = null,
    string? SustentoDocumentAuthNumber = null);
