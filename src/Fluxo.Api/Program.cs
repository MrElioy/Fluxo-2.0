using Fluxo.Core.Application;
using Fluxo.Core.Persistence;
using Fluxo.Persistence;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// La ruta relativa parte de la API y ubica el archivo en el proyecto hermano Fluxo.Persistence.
var connection = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Fluxo") ?? "Data Source=../Fluxo.Persistence/Data/fluxo.db");
connection.DataSource = Path.GetFullPath(connection.DataSource, builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(connection.DataSource)!);
var store = new SqliteFinancialStore(connection.ToString());
builder.Services.AddSingleton<IFinancialStore>(store);
builder.Services.AddTransient<AccountHandler>();
builder.Services.AddTransient<ExpenseHandler>();

var app = builder.Build();
// Fallar el arranque si no se pueden aplicar las migraciones; nunca servir con esquema incompleto.
await store.InitializeAsync(
    builder.Configuration.GetValue<Guid>("LocalUser:Id"),
    builder.Configuration["LocalUser:Name"] ?? "Local",
    builder.Configuration["LocalUser:Currency"] ?? "ARS",
    app.Lifetime.ApplicationStopping);

// Liveness: confirma que la API responde. Los endpoints financieros se incorporarán por separado.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
