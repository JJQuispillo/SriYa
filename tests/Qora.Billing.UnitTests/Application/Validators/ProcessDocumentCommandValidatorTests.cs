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

        result.ShouldHaveValidationErrorFor(x => x.Request.Items);
    }

    [Fact]
    public void Validate_InvalidIvaRate_ShouldFail()
    {
        var request = new CreateDocumentRequest(
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test" } },
            new Dictionary<string, string>(),
            [new DocumentItemDto("P1", "Prod", 1, 5.00m, 0, 12m, "2", "2")]); // 12% is no longer valid

        var command = new ProcessDocumentCommand(Guid.NewGuid(), request);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("IVA rate"));
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

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("13 digits"));
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

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("quantity"));
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

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("unit price"));
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
                MainCode: "303",
                Description: "Honorarios profesionales",
                Quantity: 1,
                UnitPrice: 100m,
                Discount: 0,
                TaxRate: taxRate,
                TaxCode: "1",
                TaxPercentageCode: "303",
                AuxiliaryCode: null,
                SustentoDocumentType: sustentoDocumentType,
                SustentoDocumentNumber: sustentoDocumentNumber,
                SustentoDocumentIssueDate: issueDate,
                SustentoDocumentAuthNumber: sustentoDocumentAuthNumber)]);

        return new ProcessDocumentCommand(Guid.NewGuid(), request);
    }

    [Fact]
    public void Validate_RetencionItem_WithValidRetentionRate_Passes()
    {
        // TaxRate=10 is in ValidRetencionRates {1, 1.75, 2, 8, 10, 30}
        var command = CreateRetencionCommand(taxRate: 10m);
        var result = _validator.TestValidate(command);

        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains("Retention rate"));
    }

    [Fact]
    public void Validate_RetencionItem_WithIvaRate_Fails()
    {
        // TaxRate=15 is a valid IVA rate but NOT a valid retención rate
        var command = CreateRetencionCommand(taxRate: 15m);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Retention rate") && e.ErrorMessage.Contains("15"));
    }

    [Fact]
    public void Validate_FacturaItem_WithIvaRate_Passes()
    {
        // For Factura, TaxRate=15 is valid (ValidIvaRates = {0, 5, 15})
        var command = CreateValidCommand(); // uses Factura + TaxRate=15
        var result = _validator.TestValidate(command);

        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains("IVA rate"));
    }

    [Fact]
    public void Validate_RetencionItem_WithMissingSustentoDocumentType_Fails()
    {
        var command = CreateRetencionCommand(sustentoDocumentType: null);
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("SustentoDocumentType"));
    }

    [Fact]
    public void Validate_RetencionItem_WithInvalidSustentoDocumentType_Fails()
    {
        // "99" is not in ValidSustentoDocumentTypes {01, 03, 04, 05, 07, 41, 43}
        var command = CreateRetencionCommand(sustentoDocumentType: "99");
        var result = _validator.TestValidate(command);

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("SustentoDocumentType") && e.ErrorMessage.Contains("99"));
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
