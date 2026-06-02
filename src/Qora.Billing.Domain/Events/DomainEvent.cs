using MediatR;

namespace Qora.Billing.Domain.Events;

/// <summary>
/// Clase base para todos los eventos de dominio del microservicio de facturación.
/// Implementa INotification para que los eventos puedan despacharse mediante MediatR.
/// </summary>
public abstract class DomainEvent : INotification
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime OccurredAt { get; } = DateTime.UtcNow;
    public abstract string EventType { get; }
}
