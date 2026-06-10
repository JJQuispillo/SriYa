using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qora.Billing.Application.DTOs;

namespace Qora.Billing.Application.Extensions;

/// <summary>
/// Calcula un hash determinista del cuerpo de una emisión, para detectar reuso de una misma
/// Idempotency-Key con un payload distinto (request_hash diferente → 422).
///
/// El hash NO depende de claveAcceso (su numericCode es aleatorio e inestable entre reintentos):
/// se computa sobre el <see cref="CreateDocumentRequest"/> ya mapeado (tipo, emisor, comprador, detalles,
/// destinatarios), que es exactamente lo que el cliente reenvía en un reintento.
/// </summary>
public static class IdempotencyHasher
{
    private static readonly JsonSerializerOptions _canonicalOptions = new()
    {
        // Orden de propiedades estable a partir del tipo record; sin indentación para minimizar variación.
        WriteIndented = false,
    };

    public static string ComputeRequestHash(CreateDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Proyección canónica: los diccionarios se ordenan por clave para que el hash no dependa del
        // orden de inserción. Listas se preservan en orden (el orden de los ítems es semánticamente parte
        // del comprobante).
        var canonical = new
        {
            tipo = request.TipoDocumento.ToString(),
            emisor = Canonicalize(request.Emisor),
            comprador = Canonicalize(request.Comprador),
            detalles = request.Detalles,
            destinatarios = request.Destinatarios,
        };

        var json = JsonSerializer.Serialize(canonical, _canonicalOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes);
    }

    private static SortedDictionary<string, string> Canonicalize(Dictionary<string, string> source)
        => new(source, StringComparer.Ordinal);
}
