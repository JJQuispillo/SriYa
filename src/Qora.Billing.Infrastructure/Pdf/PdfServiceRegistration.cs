using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Pdf;

/// <summary>
/// Métodos de extensión para registrar los servicios de generación de PDF.
/// </summary>
public static class PdfServiceRegistration
{
    /// <summary>
    /// Registra la licencia de QuestPDF y los servicios del generador de RIDE.
    /// Debe llamarse durante el arranque de la aplicación.
    /// </summary>
    public static IServiceCollection AddPdfServices(this IServiceCollection services)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        services.AddSingleton<IRideGenerator, RideGenerator>();
        return services;
    }
}
