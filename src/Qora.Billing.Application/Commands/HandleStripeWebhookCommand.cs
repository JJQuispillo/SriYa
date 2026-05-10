using MediatR;

namespace Qora.Billing.Application.Commands;

public record HandleStripeWebhookCommand(string Payload, string StripeSignature) : IRequest;
