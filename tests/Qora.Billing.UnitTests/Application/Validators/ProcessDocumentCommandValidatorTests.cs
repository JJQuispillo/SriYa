using FluentAssertions;
using FluentValidation.TestHelper;
using Qora.Billing.Application.Commands;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Validators;
using DomainDocumentType = Qora.Billing.Domain.Enums.DocumentType;

namespace Qora.Billing.UnitTests.Application.Validators;

public class ProcessDocumentCommandValidatorTests
{
    private readonly ProcessDocumentCommandValidator _validator = new();

    private static ProcessDocumentCommand CreateValidCommand()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string>
            {
                { "ruc", "1792268071001" },
                { "razonSocial", "Test Corp" }
            },
            new Dictionary<string, string>
            {
                { "identificacion", "0102030405001" },
                { "razonSocial", "Buyer Corp" }
            },
            [new DocumentItemDto("PROD001", "Test Product", 2, 10.00m, 0, 15m, "2", "4")]);

        return new ProcessDocumentCommand(Guid.NewGuid(), request);
    }

    [Fact]
    public void Validate_ValidCommand_ShouldPass()
    {
        var command = CreateValidCommand();
        var result = _validator.TestValidate(command);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyTenantId_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(),
            [new DocumentItemDto("P1", "Prod", 1, 5.00m, 0, 0m, "2", "0")]);

        var command = new ProcessDocumentCommand(Guid.Empty, request);
        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.TenantId);
    }

    [Fact]
    public void Validate_NoItems_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(),
            []);

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.ShouldHaveValidationErrorFor(x => x.Request.Detalles);
    }

    [Fact]
    public void Validate_InvalidTaxCode_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(),
            [new DocumentItemDto("P1", "Prod", 1, 5.00m, 0, 0m, "2", "99")]); // "2/99" does not exist in SRI table

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("no es válido según la tabla de códigos SRI"));
    }

    [Fact]
    public void Validate_InvalidRucFormat_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "12345" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(),
            [new DocumentItemDto("P1", "Prod", 1, 5.00m, 0, 15m, "2", "4")]);

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("13 dígitos"));
    }

    [Fact]
    public void Validate_TotalOver200WithoutBuyerId_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(), // No buyer identification
            [new DocumentItemDto("P1", "Expensive Product", 1, 250.00m, 0, 15m, "2", "4")]);

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("$200"));
    }

    [Fact]
    public void Validate_ZeroQuantity_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(),
            [new DocumentItemDto("P1", "Prod", 0, 5.00m, 0, 15m, "2", "4")]);

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("cantidad"));
    }

    [Fact]
    public void Validate_NegativeUnitPrice_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(),
            [new DocumentItemDto("P1", "Prod", 1, -5.00m, 0, 15m, "2", "4")]);

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("precio unitario"));
    }

    // ─── Batch 6: retención TaxRate and sustento field validation tests ─────────

    private static ProcessDocumentCommand CreateRetencionCommand(
        decimal taxRate = 10m,
        string? sustentoDocumentType = "01",
        string? sustentoDocumentNumber = "001-001-000000001",
        string? sustentoDocumentAuthNumber = "2401202401179226807100110010010000000991234567890",
        DateTime? sustentoDocumentIssueDate = null)
    {
        var issueDate = sustentoDocumentIssueDate ?? new DateTime(2024, 1, 10);

        var request = new CreateDocumentRequest(
            DomainDocumentType.ComprobanteRetencion,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test Corp" } },
            new Dictionary<string, string> { { "identificacion", "9999999999999" }, { "razonSocial", "Sujeto Retenido" } },
            [new DocumentItemDto(
                CodigoPrincipal: "303",
                Descripcion: "Honorarios profesionales",
                Cantidad: 1,
                PrecioUnitario: 100m,
                Descuento: 0,
                TasaImpuesto: taxRate,
                CodigoImpuesto: "1",
                CodigoPorcentaje: "303",
                CodigoAuxiliar: null,
                TipoDocSustento: sustentoDocumentType,
                NumDocSustento: sustentoDocumentNumber,
                FechaEmisionDocSustento: issueDate,
                NumAutDocSustento: sustentoDocumentAuthNumber)]);

        return new ProcessDocumentCommand(Guid.NewGuid(), request);
    }

    [Fact]
    public void Validate_RetencionItem_WithValidTaxCode_Passes()
    {
        // TaxCode "1/303" (Ret. Renta 1%) is in the SRI reference table
        var command = CreateRetencionCommand(taxRate: 10m);
        var result = _validator.TestValidate(command);

        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains("no es válido según la tabla de códigos SRI"));
    }

    [Fact]
    public void Validate_RetencionItem_WithInvalidTaxCode_Fails()
    {
        // TaxCode "1/999" does not exist in the SRI reference table
        var request = new CreateDocumentRequest(
            DomainDocumentType.ComprobanteRetencion,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test Corp" } },
            new Dictionary<string, string> { { "identificacion", "9999999999999" }, { "razonSocial", "Sujeto Retenido" } },
            [new DocumentItemDto(
                CodigoPrincipal: "999",
                Descripcion: "Honorarios profesionales",
                Cantidad: 1,
                PrecioUnitario: 100m,
                Descuento: 0,
                TasaImpuesto: 0m,
                CodigoImpuesto: "1",
                CodigoPorcentaje: "999",
                TipoDocSustento: "01",
                NumDocSustento: "001-001-000000001",
                FechaEmisionDocSustento: new DateTime(2024, 1, 10),
                NumAutDocSustento: "2401202401179226807100110010010000000991234567890")]);

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("no es válido según la tabla de códigos SRI"));
    }

    [Fact]
    public void Validate_FacturaItem_WithValidTaxCode_Passes()
    {
        // For Factura, TaxCode "2/4" (IVA 15%) is valid
        var command = CreateValidCommand(); // uses Factura + TaxCode "2"/"4"
        var result = _validator.TestValidate(command);

        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains("no es válido según la tabla de códigos SRI"));
    }

    [Fact]
    public void Validate_RetencionItem_WithMissingSustentoDocumentType_Fails()
    {
        var command = CreateRetencionCommand(sustentoDocumentType: null);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("sustento"));
    }

    [Fact]
    public void Validate_RetencionItem_WithInvalidSustentoDocumentType_Fails()
    {
        // "99" is not in ValidSustentoDocumentTypes {01, 03, 04, 05, 07, 41, 43}
        var command = CreateRetencionCommand(sustentoDocumentType: "99");
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("sustento") && e.ErrorMessage.Contains("99"));
    }

    [Fact]
    public void Validate_RetencionItem_WithAllSustentoFields_Passes()
    {
        // Happy path: all sustento fields present and valid
        var command = CreateRetencionCommand(
            taxRate: 10m,
            sustentoDocumentType: "01",
            sustentoDocumentNumber: "001-001-000000001",
            sustentoDocumentAuthNumber: "2401202401179226807100110010010000000991234567890",
            sustentoDocumentIssueDate: new DateTime(2024, 1, 10));

        var result = _validator.TestValidate(command);

        result.Errors.Should()
            .NotContain(e => e.ErrorMessage.Contains("SustentoDocument") || e.ErrorMessage.Contains("Retention rate"));
    }
}
