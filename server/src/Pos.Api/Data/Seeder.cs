using Microsoft.EntityFrameworkCore;
using Pos.Api.Domain;
using Pos.Api.Services;

namespace Pos.Api.Data;

/// <summary>
/// Datos iniciales. Base (empresa, sucursal, cajas, impuestos, admin) siempre que la BD esté vacía.
/// Datos de demostración FICTICIOS solo con Seed__Demo=true (incluye contraseñas conocidas: no usar en producción).
/// </summary>
public static class Seeder
{
    public static async Task RunAsync(AppDb db, IConfiguration cfg, ILogger log)
    {
        if (!await db.Companies.AnyAsync())
        {
            var company = new Company { Name = "Comercial Demo, S.R.L. (ficticia)", TaxId = null, CurrencyCode = "DOP", CurrencySymbol = "RD$" };
            db.Companies.Add(company);
            var branch = new Branch { CompanyId = company.Id, Name = "Sucursal Principal", Address = "Calle Ficticia #1, Santo Domingo" };
            db.Branches.Add(branch);
            var loc = new Location { BranchId = branch.Id, Name = "Almacén principal", IsDefault = true };
            db.Locations.Add(loc);
            db.Registers.AddRange(new Register { BranchId = branch.Id, LocationId = loc.Id, Name = "Caja 1" }, new Register { BranchId = branch.Id, LocationId = loc.Id, Name = "Caja 2" });
            db.Units.AddRange(new Unit { Name = "Unidad", Abbreviation = "ud" }, new Unit { Name = "Libra", Abbreviation = "lb", AllowsDecimals = true },
                new Unit { Name = "Litro", Abbreviation = "L", AllowsDecimals = true });
            // Tasas configurables. Los valores/códigos fiscales dominicanos están PENDIENTES de validar con la DGII antes de emitir comprobantes.
            db.TaxRates.AddRange(new TaxRate { Name = "ITBIS 18% (por validar)", Rate = 0.18m }, new TaxRate { Name = "Exento 0%", Rate = 0m });

            var adminPass = cfg["Seed:AdminPassword"];
            if (string.IsNullOrWhiteSpace(adminPass))
            {
                adminPass = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).Replace('+', 'a').Replace('/', 'b').TrimEnd('=');
                log.LogWarning("=== Contraseña inicial de 'admin' (se muestra UNA vez): {Pass} ===", adminPass);
            }
            db.Users.Add(new AppUser { Username = "admin", FullName = "Administrador", Role = Role.Admin, PasswordHash = PasswordHasher.Hash(adminPass), PinHash = PasswordHasher.Hash(cfg["Seed:AdminPin"] ?? "0000") });
            await db.SaveChangesAsync();
        }

        if (cfg.GetValue("Seed:Demo", false) && !await db.Products.AnyAsync())
        {
            log.LogWarning("Seed__Demo=true: cargando datos FICTICIOS con credenciales conocidas. No usar en producción.");
            var branch = await db.Branches.FirstAsync();
            var loc = await db.Locations.FirstAsync();
            var unit = await db.Units.FirstAsync(u => u.Abbreviation == "ud");
            var lb = await db.Units.FirstAsync(u => u.Abbreviation == "lb");
            var tax18 = await db.TaxRates.FirstAsync(t => t.Rate == 0.18m);
            var tax0 = await db.TaxRates.FirstAsync(t => t.Rate == 0m);

            var admin = await db.Users.FirstAsync(u => u.Username == "admin");
            var demoAdmin = cfg["Seed:AdminPassword"] ?? "Admin1234!";
            admin.PasswordHash = PasswordHasher.Hash(demoAdmin); admin.PinHash = PasswordHasher.Hash("0000");
            db.Users.AddRange(
                new AppUser { Username = "supervisor", FullName = "Sara Supervisor (demo)", Role = Role.Supervisor, PasswordHash = PasswordHasher.Hash("Demo1234!"), PinHash = PasswordHasher.Hash("1234"), BranchId = branch.Id },
                new AppUser { Username = "cajero1", FullName = "Carlos Cajero (demo)", Role = Role.Cashier, PasswordHash = PasswordHasher.Hash("Demo1234!"), BranchId = branch.Id },
                new AppUser { Username = "cajero2", FullName = "Carmen Cajera (demo)", Role = Role.Cashier, PasswordHash = PasswordHasher.Hash("Demo1234!"), BranchId = branch.Id });

            var cats = new[] { "Bebidas", "Despensa", "Lácteos", "Limpieza", "Snacks" }.Select(n => new Category { Name = n }).ToList();
            db.Categories.AddRange(cats);
            Category C(string n) => cats.First(c => c.Name == n);

            var items = new (string sku, string bar, string name, string cat, decimal cost, decimal price, decimal stock, decimal min, bool lb, bool exempt)[]
            {
                ("BEB-001","7501000000011","Agua 500 ml","Bebidas",12,25,120,24,false,false),
                ("BEB-002","7501000000028","Refresco cola 355 ml","Bebidas",28,50,96,24,false,false),
                ("BEB-003","7501000000035","Jugo naranja 1 L","Bebidas",70,120,40,10,false,false),
                ("BEB-004","7501000000042","Cerveza lata 355 ml","Bebidas",60,95,72,24,false,false),
                ("DES-001","7501000000059","Arroz selecto (libra)","Despensa",22,32,300,50,true,true),
                ("DES-002","7501000000066","Habichuelas rojas (libra)","Despensa",45,65,150,30,true,true),
                ("DES-003","7501000000073","Aceite vegetal 1 L","Despensa",150,210,36,8,false,false),
                ("DES-004","7501000000080","Azúcar crema (libra)","Despensa",28,40,200,40,true,true),
                ("DES-005","7501000000097","Pasta espagueti 400 g","Despensa",35,58,60,12,false,false),
                ("DES-006","7501000000103","Salsa de tomate 8 oz","Despensa",20,35,80,20,false,false),
                ("LAC-001","7501000000110","Leche entera 1 L","Lácteos",62,95,48,12,false,true),
                ("LAC-002","7501000000127","Queso blanco (libra)","Lácteos",180,260,25,5,true,false),
                ("LAC-003","7501000000134","Yogurt natural 1 L","Lácteos",85,130,20,6,false,false),
                ("LIM-001","7501000000141","Detergente 1 kg","Limpieza",95,150,30,8,false,false),
                ("LIM-002","7501000000158","Jabón de baño","Limpieza",25,45,100,20,false,false),
                ("LIM-003","7501000000165","Papel higiénico x4","Limpieza",70,115,50,12,false,false),
                ("LIM-004","7501000000172","Cloro 1 L","Limpieza",40,70,45,10,false,false),
                ("SNK-001","7501000000189","Galletas de soda","Snacks",18,30,90,20,false,false),
                ("SNK-002","7501000000196","Papitas fritas 40 g","Snacks",22,40,70,20,false,false),
                ("SNK-003","7501000000202","Chocolate barra","Snacks",30,55,60,15,false,false),
                ("SNK-004","7501000000219","Maní salado","Snacks",15,28,5,10,false,false),
                ("SNK-005","7501000000226","Último artículo de prueba (stock 1)","Snacks",50,90,1,1,false,false),
            };
            foreach (var i in items)
            {
                var p = new Product { Sku = i.sku, Barcode = i.bar, Name = i.name, CategoryId = C(i.cat).Id, UnitId = i.lb ? lb.Id : unit.Id, TaxRateId = i.exempt ? tax0.Id : tax18.Id, Cost = i.cost, Price = i.price, MinStock = i.min };
                db.Products.Add(p);
                db.StockLevels.Add(new StockLevel { ProductId = p.Id, LocationId = loc.Id, Quantity = i.stock });
                db.InventoryMovements.Add(new InventoryMovement { ProductId = p.Id, LocationId = loc.Id, Type = MovementType.Initial, Quantity = i.stock, BalanceAfter = i.stock, UnitCost = i.cost, Reference = "INICIAL", Reason = "Carga de demostración" });
            }
            db.Customers.AddRange(
                new Customer { Name = "María Pérez (demo)", Phone = "809-555-0101", CreditLimit = 5000, CreditDays = 15 },
                new Customer { Name = "Colmado La Esquina (demo)", Phone = "809-555-0102", CreditLimit = 20000, CreditDays = 30 },
                new Customer { Name = "Juan Sin Crédito (demo)", Phone = "809-555-0103", CreditLimit = 0 });
            db.Suppliers.AddRange(new Supplier { Name = "Distribuidora Ficticia A (demo)", CreditDays = 30 }, new Supplier { Name = "Importadora Ficticia B (demo)", CreditDays = 15 });
            await db.SaveChangesAsync();
        }
    }
}
