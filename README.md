# Fluxo 2.0

Esqueleto inicial del backend C# con ASP.NET Core. Destino preliminar: .NET 8, disponible en este equipo.

## Proyectos

- `Fluxo.Api`: entrada HTTP, contratos públicos y configuración de la aplicación.
- `Fluxo.Core`: entidades, reglas, casos de uso y contratos de persistencia. No depende de HTTP ni de EF Core.
- `Fluxo.Persistence`: implementación de los contratos del core mediante SQLite y EF Core.
- `tests/Fluxo.Tests`: ubicación reservada para pruebas del core y de integración con SQLite; todavía no contiene un proyecto de pruebas.

Api referencia Core y Persistence. Persistence referencia Core. Se ejecutará una única aplicación.

## Ejecutar

```bash
dotnet build Fluxo.sln
dotnet run --project src/Fluxo.Api --no-launch-profile --urls http://localhost:5050
```

`GET http://localhost:5050/health` confirma que la API responde; todavía no comprueba una base de datos.

## Próximo paso

Definir e implementar el recorrido de cuenta con saldo inicial, registro de gasto con varias categorías y consulta del saldo. Los importes de movimientos son positivos; el tipo determina entrada o salida. Las categorías tienen relación N:N.

Antes de crear la primera migración: elegir la versión definitiva de .NET y EF Core y la representación exacta de importes y fechas en SQLite. Incorporar `Microsoft.EntityFrameworkCore.Sqlite`, las herramientas de migración y el DbContext en Persistence. No se incluyen todavía paquetes EF Core, entidades financieras, migraciones ni frontend.
