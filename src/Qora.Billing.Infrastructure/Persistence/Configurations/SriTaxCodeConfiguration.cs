using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Qora.Billing.Domain.Entities;

namespace Qora.Billing.Infrastructure.Persistence.Configurations;

public class SriTaxCodeConfiguration : IEntityTypeConfiguration<SriTaxCode>
{
    public void Configure(EntityTypeBuilder<SriTaxCode> builder)
    {
        builder.ToTable("sri_tax_codes");

        builder.HasKey(t => new { t.TaxTypeCode, t.PercentageCode });

        builder.Property(t => t.TaxTypeCode)
            .HasColumnName("tax_type_code")
            .HasMaxLength(5)
            .IsRequired();

        builder.Property(t => t.PercentageCode)
            .HasColumnName("percentage_code")
            .HasMaxLength(5)
            .IsRequired();

        builder.Property(t => t.Rate)
            .HasColumnName("rate")
            .HasPrecision(10, 4)
            .IsRequired();

        builder.Property(t => t.Description)
            .HasColumnName("description")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(t => t.IsActive)
            .HasColumnName("is_active")
            .HasDefaultValue(true)
            .IsRequired();

        // ── Seed data: official SRI tax codes ────────────────────────────────────

        // IVA (TaxTypeCode = "2")
        builder.HasData(
            new { TaxTypeCode = "2", PercentageCode = "0",  Rate = 0m,   Description = "IVA 0%",                 IsActive = true  },
            new { TaxTypeCode = "2", PercentageCode = "2",  Rate = 12m,  Description = "IVA 12%",                IsActive = true  },
            new { TaxTypeCode = "2", PercentageCode = "3",  Rate = 14m,  Description = "IVA 14% (histórico)",    IsActive = false },
            new { TaxTypeCode = "2", PercentageCode = "4",  Rate = 15m,  Description = "IVA 15%",                IsActive = true  },
            new { TaxTypeCode = "2", PercentageCode = "5",  Rate = 5m,   Description = "IVA 5%",                 IsActive = true  },
            new { TaxTypeCode = "2", PercentageCode = "6",  Rate = 0m,   Description = "No Objeto de IVA",       IsActive = true  },
            new { TaxTypeCode = "2", PercentageCode = "7",  Rate = 0m,   Description = "Exento de IVA",          IsActive = true  },
            new { TaxTypeCode = "2", PercentageCode = "8",  Rate = 8m,   Description = "IVA 8% (histórico)",     IsActive = false },
            new { TaxTypeCode = "2", PercentageCode = "10", Rate = 10m,  Description = "IVA 10% (histórico)",    IsActive = false }
        );

        // ICE (TaxTypeCode = "3")
        builder.HasData(
            new { TaxTypeCode = "3", PercentageCode = "3011", Rate = 75m,   Description = "ICE Cigarrillos",           IsActive = true },
            new { TaxTypeCode = "3", PercentageCode = "3023", Rate = 0.15m, Description = "ICE Cerveza (L)",           IsActive = true },
            new { TaxTypeCode = "3", PercentageCode = "3041", Rate = 35m,   Description = "ICE Vehículos < $20k",      IsActive = true },
            new { TaxTypeCode = "3", PercentageCode = "3072", Rate = 9m,    Description = "ICE Bebidas alcohólicas",   IsActive = true }
        );

        // IRBPNR (TaxTypeCode = "5")
        builder.HasData(
            new { TaxTypeCode = "5", PercentageCode = "5001", Rate = 0.02m, Description = "IRBPNR", IsActive = true }
        );

        // ISD (TaxTypeCode = "6")
        builder.HasData(
            new { TaxTypeCode = "6", PercentageCode = "6001", Rate = 5m, Description = "ISD 5%", IsActive = true }
        );

        // Retención Renta (TaxTypeCode = "1")
        builder.HasData(
            new { TaxTypeCode = "1", PercentageCode = "303", Rate = 1m,    Description = "Ret. Renta 1%",    IsActive = true },
            new { TaxTypeCode = "1", PercentageCode = "304", Rate = 1.75m, Description = "Ret. Renta 1.75%", IsActive = true },
            new { TaxTypeCode = "1", PercentageCode = "312", Rate = 2m,    Description = "Ret. Renta 2%",    IsActive = true },
            new { TaxTypeCode = "1", PercentageCode = "322", Rate = 8m,    Description = "Ret. Renta 8%",    IsActive = true },
            new { TaxTypeCode = "1", PercentageCode = "332", Rate = 10m,   Description = "Ret. Renta 10%",   IsActive = true },
            new { TaxTypeCode = "1", PercentageCode = "343", Rate = 30m,   Description = "Ret. Renta 30%",   IsActive = true }
        );
    }
}
