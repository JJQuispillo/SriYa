using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Events;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.ValueObjects;
using DomainDocumentType = Qora.Billing.Domain.Enums.DocumentType;

namespace Qora.Billing.UnitTests.Domain.Entities;

public class DocumentTests
{
    private static Document CreateTestDocument()
    {
        return Document.Create(
            Guid.NewGuid(),
            DomainDocumentType.Factura,
            new Dictionary<string, string> { { "ruc", "1792268071001" }, { "razonSocial", "Test Corp" } },
            new Dictionary<string, string> { { "ruc", "0102030405001" }, { "razonSocial", "Buyer Corp" } });
    }

    [Fact]
    public void Create_ShouldInitializeWithDraftStatus()
    {
        var document = CreateTestDocument();

        document.Status.Should().Be(DocumentStatus.Draft);
        document.DocumentType.Should().Be(DomainDocumentType.Factura);
        document.RetryCount.Should().Be(0);
        document.Id.Should().NotBeEmpty();
        document.TenantId.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_ShouldRaiseDocumentCreatedEvent()
    {
        var document = CreateTestDocument();

        document.DomainEvents.Should().ContainSingle();
        Assert.IsType<DocumentCreatedEvent>(document.DomainEvents[0]);
    }

    [Fact]
    public void AddItem_WhenDraft_ShouldAddItem()
    {
        var document = CreateTestDocument();
        var item = DocumentItem.Create(
            document.Id, "PROD001", "Test Product", 2, 10.50m, 0, 15m, "2", "4");

        document.AddItem(item);

        document.Items.Should().ContainSingle();
    }

    [Fact]
    public void SetXmlContent_WhenDraft_ShouldTransitionToXmlGenerated()
    {
        var document = CreateTestDocument();
        var accessKey = GenerateTestAccessKey();

        document.SetXmlContent("<xml>test</xml>", accessKey);

        document.Status.Should().Be(DocumentStatus.XmlGenerated);
        document.XmlContent.Should().Be("<xml>test</xml>");
        (document.AccessKey == accessKey).Should().BeTrue();
    }

    [Fact]
    public void SetSignedXml_WhenXmlGenerated_ShouldTransitionToSigned()
    {
        var document = CreateTestDocument();
        document.SetXmlContent("<xml>test</xml>", GenerateTestAccessKey());

        document.SetSignedXml("<signed>test</signed>");

        document.Status.Should().Be(DocumentStatus.Signed);
        document.SignedXmlContent.Should().Be("<signed>test</signed>");
    }

    [Fact]
    public void SetSignedXml_WhenDraft_ShouldThrowDocumentValidationException()
    {
        var document = CreateTestDocument();

        var act = () => document.SetSignedXml("<signed>test</signed>");

        act.Should().Throw<DocumentValidationException>();
    }

    [Fact]
    public void MarkSentToSri_WhenSigned_ShouldTransitionToSentToSri()
    {
        var document = CreateTestDocument();
        document.SetXmlContent("<xml>test</xml>", GenerateTestAccessKey());
        document.SetSignedXml("<signed>test</signed>");

        document.MarkSentToSri();

        document.Status.Should().Be(DocumentStatus.SentToSri);
    }

    [Fact]
    public void Authorize_WhenSentToSri_ShouldTransitionToAuthorized()
    {
        var document = CreateTestDocument();
        document.SetXmlContent("<xml>test</xml>", GenerateTestAccessKey());
        document.SetSignedXml("<signed>test</signed>");
        document.MarkSentToSri();

        document.Authorize("AUTH123", DateTime.UtcNow);

        document.Status.Should().Be(DocumentStatus.Authorized);
        document.SriAuthorizationNumber.Should().Be("AUTH123");
        document.ProcessedAt.Should().NotBeNull();
        document.DomainEvents.Should().Contain(e => e is DocumentAuthorizedEvent);
    }

    [Fact]
    public void Reject_WhenSentToSri_ShouldTransitionToRejected()
    {
        var document = CreateTestDocument();
        document.SetXmlContent("<xml>test</xml>", GenerateTestAccessKey());
        document.SetSignedXml("<signed>test</signed>");
        document.MarkSentToSri();

        document.Reject("Invalid XML structure");

        document.Status.Should().Be(DocumentStatus.Rejected);
        document.ErrorMessage.Should().Be("Invalid XML structure");
        document.DomainEvents.Should().Contain(e => e is DocumentRejectedEvent);
    }

    [Fact]
    public void ScheduleRetry_ShouldIncrementRetryCount()
    {
        var document = CreateTestDocument();
        document.SetXmlContent("<xml>test</xml>", GenerateTestAccessKey());
        document.SetSignedXml("<signed>test</signed>");
        document.MarkSentToSri();

        var retryAt = DateTime.UtcNow.AddMinutes(5);
        document.ScheduleRetry(retryAt);

        document.Status.Should().Be(DocumentStatus.PendingRetry);
        document.RetryCount.Should().Be(1);
        document.NextRetryAt.Should().Be(retryAt);
    }

    [Fact]
    public void Void_WhenAuthorized_ShouldTransitionToVoided()
    {
        var document = CreateTestDocument();
        document.SetXmlContent("<xml>test</xml>", GenerateTestAccessKey());
        document.SetSignedXml("<signed>test</signed>");
        document.MarkSentToSri();
        document.Authorize("AUTH123", DateTime.UtcNow);
        document.ClearDomainEvents();

        document.Void("Customer requested cancellation");

        document.Status.Should().Be(DocumentStatus.Voided);
        document.DomainEvents.Should().ContainSingle();
        Assert.IsType<DocumentVoidedEvent>(document.DomainEvents[0]);
    }

    [Fact]
    public void Void_WhenNotAuthorized_ShouldThrowDocumentValidationException()
    {
        var document = CreateTestDocument();

        var act = () => document.Void("reason");

        act.Should().Throw<DocumentValidationException>();
    }

    [Fact]
    public void AddItem_WhenNotDraft_ShouldThrowDocumentValidationException()
    {
        var document = CreateTestDocument();
        document.SetXmlContent("<xml>test</xml>", GenerateTestAccessKey());
        var item = DocumentItem.Create(
            document.Id, "PROD001", "Test Product", 2, 10.50m, 0, 15m, "2", "4");

        var act = () => document.AddItem(item);

        act.Should().Throw<DocumentValidationException>();
    }

    private static AccessKey GenerateTestAccessKey()
    {
        var baseDigits = "180320260117922680710011001001000000012372816811";
        var weights = new[] { 2, 3, 4, 5, 6, 7 };
        var sum = 0;
        for (var i = baseDigits.Length - 1; i >= 0; i--)
        {
            var weightIndex = (baseDigits.Length - 1 - i) % weights.Length;
            sum += (baseDigits[i] - '0') * weights[weightIndex];
        }
        var remainder = sum % 11;
        var checkDigit = 11 - remainder;
        checkDigit = checkDigit switch { 11 => 0, 10 => 1, _ => checkDigit };
        return new AccessKey(baseDigits + checkDigit);
    }
}
