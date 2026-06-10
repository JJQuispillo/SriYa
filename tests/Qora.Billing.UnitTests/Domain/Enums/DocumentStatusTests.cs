using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;

namespace Qora.Billing.UnitTests.Domain.Enums;

public class DocumentStatusTests
{
    [Theory]
    [InlineData(DocumentStatus.Draft, DocumentStatus.XmlGenerated, true)]
    [InlineData(DocumentStatus.XmlGenerated, DocumentStatus.Signed, true)]
    [InlineData(DocumentStatus.Signed, DocumentStatus.SentToSri, true)]
    [InlineData(DocumentStatus.SentToSri, DocumentStatus.Authorized, true)]
    [InlineData(DocumentStatus.SentToSri, DocumentStatus.Rejected, true)]
    [InlineData(DocumentStatus.SentToSri, DocumentStatus.PendingRetry, true)]
    [InlineData(DocumentStatus.Authorized, DocumentStatus.Voided, true)]
    [InlineData(DocumentStatus.Rejected, DocumentStatus.PendingRetry, true)]
    [InlineData(DocumentStatus.Rejected, DocumentStatus.Failed, true)]
    [InlineData(DocumentStatus.PendingRetry, DocumentStatus.Signed, true)]
    [InlineData(DocumentStatus.PendingRetry, DocumentStatus.Rejected, true)]
    [InlineData(DocumentStatus.PendingRetry, DocumentStatus.Failed, true)]
    // Invalid transitions
    [InlineData(DocumentStatus.Draft, DocumentStatus.Authorized, false)]
    [InlineData(DocumentStatus.Draft, DocumentStatus.Signed, false)]
    [InlineData(DocumentStatus.Authorized, DocumentStatus.Draft, false)]
    [InlineData(DocumentStatus.Failed, DocumentStatus.Draft, false)]
    [InlineData(DocumentStatus.Voided, DocumentStatus.Draft, false)]
    [InlineData(DocumentStatus.XmlGenerated, DocumentStatus.Authorized, false)]
    public void IsValidTransition_ShouldReturnExpectedResult(
        DocumentStatus from, DocumentStatus to, bool expected)
    {
        var result = Document.IsValidTransition(from, to);

        result.Should().Be(expected);
    }
}
