using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qora.Billing.Application.Interfaces;
using Qora.Billing.Application.Settings;
using Qora.Billing.Domain.Entities;
using Qora.Billing.Domain.Enums;
using Qora.Billing.Domain.Interfaces;

namespace Qora.Billing.Infrastructure.Persistence;

/// <summary>
/// Implementación del ciclo de vida por emisor (PL-1 exportación / PL-2 borrado con retención).
///
/// Todas las operaciones corren sobre el contexto acotado por tenant (billing_app, con la GUC
/// app.current_tenant fijada por TenantContextMiddleware en la transacción ambiental). Por RLS + los
/// global query filters, sólo ven/afectan datos del emisor en contexto — NUNCA de otros tenants.
///
/// El borrado respeta el orden de las FK RESTRICT (documents→tenants): se eliminan/anonimizan primero los
/// documentos (con sus hijos en cascada) y, por defecto, el tenant se anonimiza EN SITIO (no se borra),
/// porque los comprobantes autorizados se conservan para retención fiscal y la FK RESTRICT impediría
/// borrar el tenant mientras existan. Sólo con AllowHardDeleteAuthorized=true se borra físicamente todo.
/// </summary>
public sealed class TenantLifecycleService : ITenantLifecycleService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly BillingDbContext _context;
    private readonly IRideGenerator _rideGenerator;
    private readonly LifecycleSettings _settings;
    private readonly ILogger<TenantLifecycleService> _logger;

    public TenantLifecycleService(
        BillingDbContext context,
        IRideGenerator rideGenerator,
        IOptions<LifecycleSettings> settings,
        ILogger<TenantLifecycleService> logger)
    {
        _context = context;
        _rideGenerator = rideGenerator;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<long> ExportTenantAsync(Guid tenantId, Stream output, CancellationToken cancellationToken = default)
    {
        var countingStream = new CountingStream(output);

        // leaveOpen: true → no cerramos el stream destino (el endpoint lo gestiona).
        using (var archive = new ZipArchive(countingStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteExportAsync(tenantId, archive, cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        return countingStream.BytesWritten;
    }

    public async Task<ScopedDeleteResult> DeleteTenantDataAsync(
        Guid tenantId, Stream exportOutput, CancellationToken cancellationToken = default)
    {
        // PL-2: export-always. Se exporta SIEMPRE antes de tocar cualquier fila.
        var exportSize = await ExportTenantAsync(tenantId, exportOutput, cancellationToken);

        if (_settings.ExportBeforeDelete && exportSize <= 0)
        {
            throw new InvalidOperationException(
                "El borrado con alcance exige una exportación previa, pero la exportación no produjo datos.");
        }

        // Cargamos TODOS los documentos del tenant (incluidos los ya soft-deleted, para idempotencia) con
        // sus hijos, ignorando el query filter de soft-delete pero NO el de tenant (RLS sigue acotando).
        var documents = await _context.Documents
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .Include(d => d.Items)
            .Include(d => d.Destinatarios)
                .ThenInclude(dest => dest.Items)
            .ToListAsync(cancellationToken);

        var authorizedAnonymized = 0;
        var authorizedHardDeleted = 0;
        var nonAuthorizedHardDeleted = 0;

        foreach (var document in documents)
        {
            var isAuthorized = document.Status == DocumentStatus.Authorized;

            if (isAuthorized && !_settings.AllowHardDeleteAuthorized)
            {
                // Por defecto: anonimizar + soft-delete (retención fiscal). Idempotente.
                if (document.DeletedAt is null || !document.IsAnonymized)
                {
                    document.Anonymize();
                    _context.Documents.Update(document);
                }
                authorizedAnonymized++;
            }
            else
            {
                // No autorizados → hard delete permitido. Autorizados → sólo si AllowHardDeleteAuthorized.
                // Los hijos (items/destinatarios/dest-items/events) se eliminan en cascada por FK.
                _context.Documents.Remove(document);
                if (isAuthorized) authorizedHardDeleted++;
                else nonAuthorizedHardDeleted++;
            }
        }

        // Las claves de idempotencia del tenant ya no son útiles tras el borrado: se eliminan siempre.
        var idempotencyKeys = await _context.IdempotencyKeys
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId)
            .ToListAsync(cancellationToken);
        if (idempotencyKeys.Count > 0)
            _context.IdempotencyKeys.RemoveRange(idempotencyKeys);

        await _context.SaveChangesAsync(cancellationToken);

        var tenantAnonymized = false;
        var tenantHardDeleted = false;

        // ¿Quedan documentos retenidos (autorizados anonimizados)? Si es así, la FK RESTRICT impide borrar
        // el tenant: lo anonimizamos EN SITIO. Si no queda ninguno y se permite el hard delete, lo borramos
        // físicamente (cascada de api_keys + electronic_signatures).
        var remainingDocuments = await _context.Documents
            .IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == tenantId, cancellationToken);

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is not null)
        {
            if (remainingDocuments == 0 && _settings.AllowHardDeleteAuthorized)
            {
                _context.Tenants.Remove(tenant);
                tenantHardDeleted = true;
            }
            else
            {
                tenant.Anonymize();
                _context.Tenants.Update(tenant);
                tenantAnonymized = true;
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Borrado con alcance del tenant {TenantId}: {Anon} autorizados anonimizados, {HardAuth} autorizados borrados, " +
            "{NonAuth} no-autorizados borrados, tenant {TenantDisposition}.",
            tenantId, authorizedAnonymized, authorizedHardDeleted, nonAuthorizedHardDeleted,
            tenantHardDeleted ? "borrado" : tenantAnonymized ? "anonimizado" : "intacto");

        return new ScopedDeleteResult(
            tenantId,
            authorizedAnonymized,
            authorizedHardDeleted,
            nonAuthorizedHardDeleted,
            tenantAnonymized,
            tenantHardDeleted,
            exportSize);
    }

    private async Task WriteExportAsync(Guid tenantId, ZipArchive archive, CancellationToken cancellationToken)
    {
        // Perfil del tenant (acotado por RLS al tenant en contexto).
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        // Documentos del tenant CON sus hijos. Incluimos los soft-deleted para que la exportación sea
        // completa también dentro de un borrado con retención (IgnoreQueryFilters salta el filtro de
        // soft-delete; RLS + el predicado de tenant siguen acotando al emisor).
        var documents = await _context.Documents
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .Include(d => d.Items)
            .Include(d => d.Destinatarios)
                .ThenInclude(dest => dest.Items)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        var documentIds = documents.Select(d => d.Id).ToList();

        var events = await _context.DocumentEvents
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(cancellationToken);

        // Certificados: SOLO metadata. NUNCA exportamos la clave privada descifrada (D5). Tampoco
        // los bytes del .p12 ni la contraseña cifrada — sólo datos no sensibles para inventario.
        var signatures = await _context.ElectronicSignatures
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        // API keys: SOLO metadata (nunca la clave en claro; el hash tampoco se exporta).
        var apiKeys = await _context.ApiKeys
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

        var idempotencyKeys = await _context.IdempotencyKeys
            .IgnoreQueryFilters()
            .Where(k => k.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var manifest = new
        {
            tenantId,
            exportedAt = DateTime.UtcNow,
            counts = new
            {
                documents = documents.Count,
                events = events.Count,
                certificates = signatures.Count,
                apiKeys = apiKeys.Count,
                idempotencyKeys = idempotencyKeys.Count,
            },
            note = "Datos de un único emisor (RLS-scoped). Las claves privadas de los certificados y los "
                 + "hashes de API key NO se incluyen.",
        };

        var data = new
        {
            tenant = tenant is null ? null : new
            {
                id = tenant.Id,
                ruc = tenant.Ruc.Value,
                businessName = tenant.BusinessName,
                tradeName = tenant.TradeName,
                contactEmail = tenant.ContactEmail,
                isActive = tenant.IsActive,
                createdAt = tenant.CreatedAt,
                updatedAt = tenant.UpdatedAt,
            },
            documents = documents.Select(d => new
            {
                id = d.Id,
                documentType = d.DocumentType.ToString(),
                accessKey = d.AccessKey?.Value,
                estab = d.Estab,
                ptoEmision = d.PtoEmision,
                secuencial = d.Secuencial,
                status = d.Status.ToString(),
                sriAuthorizationNumber = d.SriAuthorizationNumber,
                sriAuthorizationDate = d.SriAuthorizationDate,
                issuerInfo = d.IssuerInfo,
                buyerInfo = d.BuyerInfo,
                deletedAt = d.DeletedAt,
                isAnonymized = d.IsAnonymized,
                createdAt = d.CreatedAt,
                processedAt = d.ProcessedAt,
                items = d.Items.Select(i => new
                {
                    i.Id,
                    i.MainCode,
                    i.AuxiliaryCode,
                    i.Description,
                    i.Quantity,
                    i.UnitPrice,
                    i.Discount,
                    i.Subtotal,
                    i.TaxRate,
                    i.TaxCode,
                    i.TaxPercentageCode,
                }),
                destinatarios = d.Destinatarios.Select(dest => new
                {
                    dest.Id,
                    dest.IdentificacionDestinatario,
                    dest.RazonSocialDestinatario,
                    dest.DirDestinatario,
                    dest.MotivoTraslado,
                    dest.RucTransportista,
                    items = dest.Items.Select(di => new
                    {
                        di.Id,
                        di.CodigoInterno,
                        di.DescripcionDetalle,
                        di.CantidadDetalle,
                    }),
                }),
            }),
            events = events.Select(e => new
            {
                e.Id,
                e.DocumentId,
                eventType = e.EventType.ToString(),
                e.Description,
                e.OccurredAt,
            }),
            certificates = signatures.Select(s => new
            {
                s.Id,
                s.OwnerName,
                s.ExpiresAt,
                s.IsActive,
                isExpired = s.IsExpired(),
                certificateSizeBytes = s.CertificateData.Length,
                s.CreatedAt,
            }),
            apiKeys = apiKeys.Select(k => new
            {
                k.Id,
                k.Name,
                k.IsActive,
                k.ExpiresAt,
                k.CreatedAt,
            }),
            idempotencyKeys = idempotencyKeys.Select(k => new
            {
                k.Id,
                k.Key,
                k.Status,
                k.DocumentId,
                k.ExpiresAt,
                k.CreatedAt,
            }),
        };

        await WriteJsonEntryAsync(archive, "manifest.json", manifest, cancellationToken);
        await WriteJsonEntryAsync(archive, "data.json", data, cancellationToken);

        // Artefactos por documento: XML firmado (o sin firmar como fallback) + RIDE para los autorizados.
        foreach (var document in documents)
        {
            var name = document.AccessKey?.Value ?? document.Id.ToString();

            var xml = document.SignedXmlContent ?? document.XmlContent;
            if (!string.IsNullOrEmpty(xml))
            {
                await WriteTextEntryAsync(archive, $"xml/{name}.xml", xml, cancellationToken);
            }

            if (document.Status == DocumentStatus.Authorized)
            {
                try
                {
                    var pdf = await _rideGenerator.GeneratePdfAsync(document, cancellationToken);
                    var entry = archive.CreateEntry($"ride/{name}.pdf", CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(pdf, cancellationToken);
                }
                catch (Exception ex)
                {
                    // El RIDE es best-effort: si falla la generación no abortamos la exportación.
                    _logger.LogWarning(ex, "No se pudo generar el RIDE para el documento {DocumentId} en la exportación.", document.Id);
                }
            }
        }
    }

    private static async Task WriteJsonEntryAsync(
        ZipArchive archive, string entryName, object payload, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await JsonSerializer.SerializeAsync(entryStream, payload, _jsonOptions, cancellationToken);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive, string entryName, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await entryStream.WriteAsync(bytes, cancellationToken);
    }

    /// <summary>
    /// Envoltura de stream que cuenta los bytes escritos sin bufferizar (streaming real). Permite reportar
    /// el tamaño exportado para el seguro export-always de PL-2.
    /// </summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public long BytesWritten { get; private set; }

        public CountingStream(Stream inner) => _inner = inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
