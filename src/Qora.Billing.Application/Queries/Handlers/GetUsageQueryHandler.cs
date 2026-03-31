using MediatR;
using Qora.Billing.Application.DTOs;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Application.Queries.Handlers;

public class GetUsageQueryHandler : IRequestHandler<GetUsageQuery, UsageResponse>
{
    private readonly IUsageRecordRepository _usageRecordRepository;

    public GetUsageQueryHandler(IUsageRecordRepository usageRecordRepository)
    {
        _usageRecordRepository = usageRecordRepository;
    }

    public async Task<UsageResponse> Handle(GetUsageQuery query, CancellationToken cancellationToken)
    {
        var period = query.Period ?? DateTime.UtcNow.ToString("yyyy-MM");
        var count = await _usageRecordRepository.CountByTenantAndPeriodAsync(
            query.TenantId, period, cancellationToken);

        return new UsageResponse(query.TenantId, period, count);
    }
}
