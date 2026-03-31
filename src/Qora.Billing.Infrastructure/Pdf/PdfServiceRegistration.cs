using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Pdf;

/// <summary>
/// Extension methods for registering PDF generation services.
/// </summary>
public static class PdfServiceRegistration
{
    /// <summary>
    /// Registers QuestPDF license and RIDE generator services.
    /// Must be called during application startup.
    /// </summary>
    public static IServiceCollection AddPdfServices(this IServiceCollection services)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        services.AddSingleton<IRideGenerator, RideGenerator>();
        return services;
    }
}
