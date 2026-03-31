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
    decimal TaxRate,
    string TaxCode,
    string TaxPercentageCode,
    string? AuxiliaryCode = null,
    string? SustentoDocumentType = null,
    string? SustentoDocumentNumber = null,
    DateTime? SustentoDocumentIssueDate = null,
    string? SustentoDocumentAuthNumber = null);
