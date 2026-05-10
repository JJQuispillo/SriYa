using System.Security.Cryptography;
using System.Text;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class RegisterTenantCommandHandler : IRequestHandler<RegisterTenantCommand, RegisterTenantResponse>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IApiKeyRepository _apiKeyRepository;
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly IPlanRepository _planRepository;
    private readonly IEmailService _emailService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ApiKeySettings _apiKeySettings;
    private readonly ILogger<RegisterTenantCommandHandler> _logger;

    public RegisterTenantCommandHandler(
        ITenantRepository tenantRepository,
        IApiKeyRepository apiKeyRepository,
        ISubscriptionRepository subscriptionRepository,
        IPlanRepository planRepository,
        IEmailService emailService,
        IUnitOfWork unitOfWork,
        IOptions<ApiKeySettings> apiKeySettings,
        ILogger<RegisterTenantCommandHandler> logger)
    {
        _tenantRepository = tenantRepository;
        _apiKeyRepository = apiKeyRepository;
        _subscriptionRepository = subscriptionRepository;
        _planRepository = planRepository;
        _emailService = emailService;
        _unitOfWork = unitOfWork;
        _apiKeySettings = apiKeySettings.Value;
        _logger = logger;
    }

    public async Task<RegisterTenantResponse> Handle(RegisterTenantCommand command, CancellationToken cancellationToken)
    {
        // Check for duplicate RUC
        var existing = await _tenantRepository.GetByRucAsync(command.Ruc, cancellationToken);
        if (existing is not null)
            throw new BillingDomainException($"Ya existe un tenant con el RUC '{command.Ruc}'.");

        // Resolve the free/trial plan
        var plan = await _planRepository.GetBySlugAsync("free", cancellationToken);
        if (plan is null)
        {
            // Fallback: pick the first active plan with DocumentLimit == 50, or just the first active plan
            var allPlans = await _planRepository.GetAllActiveAsync(cancellationToken);
            plan = allPlans.FirstOrDefault(p => p.DocumentLimit == 50)
                ?? allPlans.FirstOrDefault()
                ?? throw new BillingDomainException("No hay planes activos configurados en el sistema.");
        }

        // Create tenant
        var tenant = Tenant.Create(command.Ruc, command.BusinessName, command.TradeName, command.ContactEmail);
        await _tenantRepository.CreateAsync(tenant, cancellationToken);

        // Create trial subscription
        var trialEndsAt = DateTime.UtcNow.AddDays(14);
        var subscription = Subscription.Create(
            tenant.Id,
            plan.Id,
            SubscriptionStatus.Trial,
            trialEndsAt);
        await _subscriptionRepository.AddAsync(subscription, cancellationToken);

        // Link subscription to tenant
        tenant.SetSubscription(subscription.Id);

        // Generate API key (same logic as CreateApiKeyCommandHandler)
        var prefix = _apiKeySettings.Prefix;
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var randomPart = Convert.ToBase64String(randomBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        var plaintextKey = $"{prefix}{randomPart}";
        var keyHash = HashApiKey(plaintextKey);

        var apiKey = ApiKey.Create(
            tenant.Id,
            keyHash,
            "default",
            expiresAt: null);
        await _apiKeyRepository.CreateAsync(apiKey, cancellationToken);

        // Persist everything
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Tenant {TenantId} registered with trial plan {PlanName}, trial ends {TrialEndsAt}",
            tenant.Id, plan.Name, trialEndsAt);

        // Fire-and-forget welcome email (best-effort, never blocks registration)
        _ = SendWelcomeEmailAsync(tenant, plan.Name, trialEndsAt);

        return new RegisterTenantResponse(
            tenant.Id,
            plaintextKey,
            trialEndsAt,
            $"Tenant registrado exitosamente. Período de prueba hasta {trialEndsAt:yyyy-MM-dd}. " +
            "Guarda tu API key — no podrás verla de nuevo.");
    }

    private async Task SendWelcomeEmailAsync(Tenant tenant, string planName, DateTime trialEndsAt)
    {
        try
        {
            // IEmailService.SendDocumentEmailAsync is document-centric; a welcome email
            // requires a different message. We log the intent here and rely on a future
            // dedicated welcome-email method. For now this is a no-op placeholder that
            // keeps the pattern intact without blocking registration.
            _logger.LogInformation(
                "Welcome email pending for tenant {TenantId} ({Email}), plan {Plan}, trial until {TrialEndsAt}",
                tenant.Id, tenant.ContactEmail, planName, trialEndsAt);
        }
        catch (Exception ex)
        {
            // Fire-and-forget: never propagate failures
            _logger.LogWarning(ex, "Welcome email dispatch failed for tenant {TenantId}", tenant.Id);
        }

        await Task.CompletedTask;
    }

    private static string HashApiKey(string plainTextKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainTextKey));
        return Convert.ToHexStringLower(bytes);
    }
}
