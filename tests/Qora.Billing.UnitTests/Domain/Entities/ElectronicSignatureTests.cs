using FluentAssertions;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Exceptions;

namespace Qora.Billing.UnitTests.Domain.Entities;

public class ElectronicSignatureTests
{
    [Fact]
    public void Create_ShouldInitializeCorrectly()
    {
        var tenantId = Guid.NewGuid();
        var certData = new byte[] { 1, 2, 3 };
        var expiresAt = DateTime.UtcNow.AddYears(1);

        var signature = ElectronicSignature.Create(
            tenantId, certData, "encryptedPwd", "Owner Name", expiresAt);

        signature.TenantId.Should().Be(tenantId);
        signature.CertificateData.Should().BeEquivalentTo(certData);
        signature.OwnerName.Should().Be("Owner Name");
        signature.IsActive.Should().BeTrue();
    }

    [Fact]
    public void EnsureValid_WhenExpired_ShouldThrowCertificateExpiredException()
    {
        var signature = ElectronicSignature.Create(
            Guid.NewGuid(), [1, 2, 3], "pwd", "Owner",
            DateTime.UtcNow.AddDays(-1));

        var act = () => signature.EnsureValid();

        act.Should().Throw<CertificateExpiredException>();
    }

    [Fact]
    public void EnsureValid_WhenInactive_ShouldThrowCertificateExpiredException()
    {
        var signature = ElectronicSignature.Create(
            Guid.NewGuid(), [1, 2, 3], "pwd", "Owner",
            DateTime.UtcNow.AddYears(1));
        signature.Deactivate();

        var act = () => signature.EnsureValid();

        act.Should().Throw<CertificateExpiredException>();
    }

    [Fact]
    public void EnsureValid_WhenActiveAndNotExpired_ShouldNotThrow()
    {
        var signature = ElectronicSignature.Create(
            Guid.NewGuid(), [1, 2, 3], "pwd", "Owner",
            DateTime.UtcNow.AddYears(1));

        var act = () => signature.EnsureValid();

        act.Should().NotThrow();
    }
}
