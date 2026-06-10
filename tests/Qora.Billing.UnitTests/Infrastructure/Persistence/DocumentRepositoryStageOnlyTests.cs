using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Qora.Billing.UnitTests.Infrastructure.Persistence;

/// <summary>
/// Guarda arquitectónica del contrato stage-only de <c>IDocumentRepository.UpdateAsync</c>
/// (sri-emision-atomicidad design D7 / T-EMI-024 / T-EMI-034). Como NetArchTest sólo verifica
/// dependencias de tipos (no secuencias de llamadas), se realiza el intento de la regla con un
/// escaneo de fuentes: cada call site de <c>UpdateAsync(</c> en los archivos que mutan documentos
/// DEBE ir seguido, dentro de pocas líneas, por un <c>SaveChangesAsync</c>. Un 4º call site que
/// olvide el guardado reintroduce el bug N1 y hace fallar este test.
/// </summary>
public class DocumentRepositoryStageOnlyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Qora.Billing.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static IEnumerable<object[]> EnforcedCallSites() => new[]
    {
        new object[] { "src/Qora.Billing.Infrastructure/BackgroundServices/SriRetryService.cs" },
        new object[] { "src/Qora.Billing.Infrastructure/BackgroundServices/SriReconciliationService.cs" },
        new object[] { "src/Qora.Billing.Application/Commands/Handlers/ProcessDocumentCommandHandler.cs" },
    };

    [Theory]
    [MemberData(nameof(EnforcedCallSites))]
    public void EveryUpdateAsyncCallSite_IsFollowedBySaveChangesAsync(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"el archivo {relativePath} debe existir");
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            // Sólo nos interesan los call sites del repositorio: UpdateAsync(document...) o repo.UpdateAsync(...).
            // Excluimos la declaración del método y las firmas de interfaz/inheritdoc.
            if (!Regex.IsMatch(lines[i], @"\.UpdateAsync\(") )
                continue;

            // Excluye UpdateAsync de IElectronicSignatureRepository (no es el contrato de documentos).
            if (Regex.IsMatch(lines[i], @"signatureRepository\.UpdateAsync|_signatureRepository\.UpdateAsync"))
                continue;

            // Ventana de las siguientes 4 líneas (cubre el patrón PersistAsync y el inline await+await).
            var window = string.Join('\n', lines.Skip(i).Take(5));
            window.Should().MatchRegex(@"SaveChangesAsync",
                $"el call site de UpdateAsync en {relativePath}:{i + 1} debe ir seguido de SaveChangesAsync (contrato N1)");
        }
    }
}
