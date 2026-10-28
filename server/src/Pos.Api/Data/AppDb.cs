using Microsoft.EntityFrameworkCore;
using Pos.Api.Domain;

namespace Pos.Api.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Register> Registers => Set<Register>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<TaxRate> TaxRates => Set<TaxRate>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();
    public DbSet<SaleReturn> Returns => Set<SaleReturn>();
    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();
    public DbSet<ReturnRefund> ReturnRefunds => Set<ReturnRefund>();
    public DbSet<CustomerPayment> CustomerPayments => Set<CustomerPayment>();
    public DbSet<CustomerPaymentAllocation> CustomerPaymentAllocations => Set<CustomerPaymentAllocation>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseLine> PurchaseLines => Set<PurchaseLine>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();
    public DbSet<DocumentCounter> DocumentCounters => Set<DocumentCounter>();
    public DbSet<BackupRecord> Backups => Set<BackupRecord>();
    public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Dinero y cantidades: siempre decimal (numeric), nunca double.
        b.Properties<decimal>().HaveColumnType("numeric(18,4)");
        b.Properties<DateTime>().HaveColumnType("timestamp with time zone");
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<StockLevel>().HasKey(x => new { x.ProductId, x.LocationId });
        m.Entity<DocumentCounter>().HasKey(x => x.Key);

        m.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        m.Entity<Product>().HasIndex(x => x.Sku).IsUnique();
        m.Entity<Product>().HasIndex(x => x.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL");
        m.Entity<Product>().HasIndex(x => x.Name);

        // Idempotencia: una operación del cliente = un documento.
        m.Entity<Sale>().HasIndex(x => x.OperationKey).IsUnique();
        m.Entity<Sale>().HasIndex(x => x.Number).IsUnique();
        m.Entity<Sale>().HasIndex(x => x.CreatedAt);
        m.Entity<SaleReturn>().HasIndex(x => x.OperationKey).IsUnique();
        m.Entity<CustomerPayment>().HasIndex(x => x.OperationKey).IsUnique();
        m.Entity<Purchase>().HasIndex(x => x.OperationKey).IsUnique();
        m.Entity<SupplierPayment>().HasIndex(x => x.OperationKey).IsUnique();
        m.Entity<Quote>().HasIndex(x => x.Number).IsUnique();

        // Un solo turno abierto por caja (garantizado por la base de datos).
        m.Entity<Shift>().HasIndex(x => x.RegisterId).IsUnique().HasFilter("\"Status\" = 1");

        m.Entity<InventoryMovement>().HasIndex(x => new { x.ProductId, x.CreatedAt });
        m.Entity<CashMovement>().HasIndex(x => x.ShiftId);
        m.Entity<AuditLog>().HasIndex(x => x.At);
        m.Entity<OutboxEvent>().HasIndex(x => x.ProcessedAt);

        m.Entity<Shift>().HasOne(x => x.OpenedBy).WithMany().HasForeignKey(x => x.OpenedByUserId);
        m.Entity<Sale>().HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.SaleId);
        m.Entity<Sale>().HasMany(s => s.Payments).WithOne().HasForeignKey(p => p.SaleId);
        m.Entity<Quote>().HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.QuoteId);
        m.Entity<SaleReturn>().HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.ReturnId);
        m.Entity<SaleReturn>().HasMany(s => s.Refunds).WithOne().HasForeignKey(l => l.ReturnId);
        m.Entity<Purchase>().HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.PurchaseId);
        m.Entity<CustomerPayment>().HasMany(s => s.Allocations).WithOne().HasForeignKey(l => l.CustomerPaymentId);

        // Las ventas confirmadas nunca se borran en cascada.
        foreach (var fk in m.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            if (fk.DeleteBehavior == DeleteBehavior.Cascade && fk.DeclaringEntityType.ClrType != typeof(object))
                fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
