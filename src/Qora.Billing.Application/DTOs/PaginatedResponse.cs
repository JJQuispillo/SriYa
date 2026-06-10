namespace Qora.Billing.Application.DTOs;

public record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int Pagina,
    int TamanoPagina,
    int Total,
    int TotalPaginas);
