using MediatR;
using Qora.Billing.Domain.Exceptions;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Commands.Handlers;

public class RevokeApiKeyCommandHandler : IRequestHandler<RevokeApiKeyCommand, bool>
{
    private readonly IApiKeyRepository _apiKeyRepository;
    private readonly IUnitOfWork _unitOfWork;

    public RevokeApiKeyCommandHandler(IApiKeyRepository apiKeyRepository, IUnitOfWork unitOfWork)
    {
        _apiKeyRepository = apiKeyRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> Handle(RevokeApiKeyCommand command, CancellationToken cancellationToken)
    {
        var keys = await _apiKeyRepository.GetByTenantIdAsync(command.TenantId, cancellationToken);
        var apiKey = keys.FirstOrDefault(k => k.Id == command.ApiKeyId);

        if (apiKey is null)
            throw new BillingDomainException($"API key {command.ApiKeyId} no encontrada para el tenant {command.TenantId}.");

        apiKey.Deactivate();
        await _apiKeyRepository.UpdateAsync(apiKey, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
