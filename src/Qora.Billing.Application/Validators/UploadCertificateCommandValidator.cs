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
            .WithMessage("El TenantId es requerido.");

        RuleFor(x => x.CertificateData)
            .NotNull()
            .WithMessage("Los datos del certificado son requeridos.")
            .Must(data => data is { Length: > 0 })
            .WithMessage("Los datos del certificado no pueden estar vacíos.")
            .Must(data => data is null || data.Length <= MaxCertificateSizeBytes)
            .WithMessage($"El archivo del certificado no debe exceder los {MaxCertificateSizeBytes / (1024 * 1024)} MB.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("La contraseña del certificado es requerida.");

        RuleFor(x => x.OwnerName)
            .NotEmpty()
            .WithMessage("El nombre del propietario es requerido.")
            .MaximumLength(200)
            .WithMessage("El nombre del propietario no debe exceder los 200 caracteres.");
    }
}
