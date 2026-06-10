using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Qora.Billing.Application.Behaviors;

namespace Qora.Billing.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        // Registra los handlers de MediatR de este assembly
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));

        // Registra los validadores de FluentValidation de este assembly
        services.AddValidatorsFromAssembly(assembly);

        // Registra los behaviors del pipeline (el orden importa: primero se ejecuta la validación, luego el logging)
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

        return services;
    }
}
