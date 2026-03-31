using MediatR;

namespace Qora.Billing.Domain.Events;

/// <summary>
/// Base class for all domain events in the billing microservice.
/// Implements INotification so events can be dispatched via MediatR.
/// </summary>
public abstract class DomainEvent : INotification
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
    public abstract string EventType { get; }
}
