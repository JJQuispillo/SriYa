using FluentValidation;
using Qora.Billing.Application.Commands;

namespace Qora.Billing.Application.Validators;

public class UploadCertificateCommandValidator : AbstractValidator<UploadCertificateCommand>
{
    private const int MaxCertificateSizeBytes = 10 * 1024 * 1024; // 10 MB

    public UploadCertificateCommandValidator()
    {
        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("TenantId is required.");

        RuleFor(x => x.CertificateData)
            .NotNull()
            .WithMessage("Certificate data is required.")
            .Must(data => data is { Length: > 0 })
            .WithMessage("Certificate data must not be empty.")
            .Must(data => data is null || data.Length <= MaxCertificateSizeBytes)
            .WithMessage($"Certificate file must not exceed {MaxCertificateSizeBytes / (1024 * 1024)} MB.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("Certificate password is required.");

        RuleFor(x => x.OwnerName)
            .NotEmpty()
            .WithMessage("Owner name is required.")
            .MaximumLength(200)
            .WithMessage("Owner name must not exceed 200 characters.");
    }
}
