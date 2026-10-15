# Fluxo 2.0

Backend C# con ASP.NET Core sobre .NET 8. Persistencia SQLite mediante EF Core 9.0.8.

## Proyectos

- `Fluxo.Api`: entrada HTTP, contratos públicos y configuración de la aplicación.
- `Fluxo.Core`: entidades, reglas, casos de uso y contratos de persistencia. No depende de HTTP ni de EF Core.
- `Fluxo.Persistence`: implementación de los contratos del core mediante SQLite y EF Core.
- `tests/Fluxo.Tests`: pruebas ejecutables del core y de integración con archivos SQLite temporales, sin framework de pruebas externo.

Api referencia Core y Persistence. Persistence referencia Core. Se ejecutará una única aplicación.

## Ejecutar

```bash
dotnet build Fluxo.sln -m:1
dotnet run --project src/Fluxo.Api --no-launch-profile --urls http://localhost:5050
```

`GET http://localhost:5050/health` confirma que la API responde. Al arrancar se aplican las migraciones y se crea el usuario local si no existe; `/health` sigue siendo una comprobación de liveness, sin consultar SQLite en cada llamada.

## Core implementado

- `Domain`: cuentas, categorías dinámicas y movimientos inmutables. Importes `decimal` positivos; el tipo determina entrada o salida. La moneda de un gasto proviene de la cuenta.
- `Application`: creación de cuenta con saldo inicial, registro de gasto con varias categorías y consulta del saldo de una cuenta.
- `Persistence/IFinancialStore`: contrato de lectura y guardado implementado por `SqliteFinancialStore`.

El saldo es saldo inicial + ingresos − gastos, sin conversión de moneda. El saldo inicial es un valor de apertura, no un ingreso ganado. Cada movimiento efectivo se incorpora una sola vez; no se incluyen proyecciones.

Los gastos validan propietario, cuenta no archivada, importe mayor que cero y categorías existentes del mismo usuario. Los identificadores de categorías repetidos se deduplican; se permite no enviar categorías. Descripción opcional, normalizada con `Trim`. No se exige saldo suficiente para registrar un hecho efectivo. Las cuentas archivadas conservan consultas de saldo. Estos son criterios iniciales de implementación para los puntos abiertos del vault.

Los instantes se reciben como `DateTimeOffset`, sin restringir fechas ni redondear importes. La moneda es un código requerido normalizado a mayúsculas; no se incorpora todavía un catálogo monetario. La representación física se detalla debajo.

Cada gasto recibe un UUID de operación separado del ID del movimiento. Un reintento del mismo usuario con los mismos datos devuelve el movimiento original; otros datos producen `OperationConflict`. El orden y las repeticiones de categorías no cambian la identidad de los datos. Persistencia garantiza esta regla también bajo concurrencia, guardando identidad, movimiento y asociaciones en una transacción con unicidad por usuario y operación. La consulta previa del handler no sustituye esa garantía.

Los rechazos de negocio usan `DomainException.Code`. Los fallos técnicos y la cancelación se propagan como excepciones; la API deberá adaptarlos sin exponer detalles internos. Los handlers solo devuelven éxito tras el guardado confirmado.

## Persistencia implementada

- `FluxoDbContext.cs`: modelo físico, conversores, claves foráneas e índices.
- `StorageRecords.cs`: registros de almacenamiento separados de las entidades del dominio.
- `SqliteFinancialStore.cs`: implementación de `IFinancialStore`; crea un contexto por operación.
- `Migrations/`: migración inicial y snapshot, generados con la herramienta local `dotnet-ef` 9.0.8.
- `FluxoDbContextFactory.cs`: configuración de diseño para generar migraciones sin arrancar la API.

Tablas: `Users`, `Accounts`, `Categories`, `Movements`, `MovementCategories` y `ExpenseOperations`. La asociación de categorías tiene clave compuesta para impedir duplicados. El historial está protegido contra eliminación de referencias mediante claves foráneas restrictivas. No se crean tablas de operaciones no estáticas hasta definir sus contratos de dominio.

Importes y saldos iniciales se guardan como `TEXT` decimal con cultura invariante, sin pasar por `double` ni redondear. Las sumas se realizan en C#. Fechas: `TEXT` en formato round-trip `O`, conservando ticks y offset; GUID: `TEXT`; tipos y booleanos: `INTEGER`. No se deben aplicar sumas ni orden numérico o temporal directamente a estas columnas de texto. La comprobación SQL de signo convierte temporalmente el importe a número para rechazar valores no positivos; no participa del cálculo del saldo.

`SaveExpenseAsync` abre una transacción SQLite inmediata antes de consultar UUID, cuenta y categorías. El proveedor espera su turno de escritura hasta el timeout configurado. Dentro de la transacción se revalidan propietario, estado de cuenta, moneda y categorías. La clave primaria `(UserId, OperationId)` respalda la unicidad; una solicitud idéntica devuelve el resultado anterior y una diferente produce `OperationConflict`. Una excepción antes del commit revierte todo el conjunto.

La API registra el store y los handlers en inyección de dependencias. `appsettings.json` configura `ConnectionStrings:Fluxo` y `LocalUser` (`Id`, `Name`, `Currency`). El ID local es estable: cambiarlo crea otra identidad, no transfiere cuentas existentes. La creación inicial no sobrescribe el nombre ni la moneda preferida de un usuario existente.

El archivo por defecto es `src/Fluxo.Persistence/Data/fluxo.db`. La configuración usa `../Fluxo.Persistence/Data/fluxo.db`, resuelta desde el content root de la API. Al publicar fuera del repositorio, configurar una ruta absoluta mediante `ConnectionStrings__Fluxo`. Su carpeta se crea al arrancar y los archivos de datos están excluidos de Git. Se puede cambiar con `ConnectionStrings__Fluxo`. La API falla al arrancar si la migración o inicialización falla. No hay endpoints financieros todavía.

`SaveCategoryAsync` permite insertar categorías desde código para poblar el almacenamiento; no añade un caso de uso ni endpoint de gestión. Un ID de cuenta o categoría ya existente se rechaza sin sobrescribir datos.

El contrato conserva firmas `Task` y transmite cancelación a EF. Microsoft.Data.Sqlite ejecuta la E/S de forma síncrona: un token no garantiza interrumpir inmediatamente una espera de bloqueo; el timeout limita esa espera. Ver [limitaciones asíncronas](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [transacciones SQLite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions) y [limitaciones del proveedor EF](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations).

## Migraciones

La API aplica las migraciones pendientes al iniciar. Para generar la siguiente migración después de cambiar el modelo:

```bash
dotnet tool restore
dotnet ef migrations add NombreDelCambio --project src/Fluxo.Persistence
```

Ejecutar las herramientas desde la raíz del repositorio: la factoría de diseño usa `src/Fluxo.Persistence/Data/fluxo.db`, el mismo archivo que la API. Para aplicar una migración manualmente, indicar explícitamente la ruta del archivo de destino:

```bash
dotnet ef database update --project src/Fluxo.Persistence --connection "Data Source=/ruta/absoluta/fluxo.db;Foreign Keys=True"
```

La migración inicial crea una base nueva. Antes de evolucionar una base con datos reales, preparar y comprobar el respaldo; la prueba actual no sustituye una restauración operativa ni valida cambios de esquema futuros.

## Verificar

```bash
dotnet build Fluxo.sln -m:1
dotnet run --project tests/Fluxo.Tests --no-build
```

Las pruebas se ejecutan con `dotnet run`, no con `dotnet test`. Cubren el recorrido cuenta/gasto/saldo, importes exactos, validaciones, reintentos y propagación de fallos. `SqlitePersistenceTests` verifica migración reaplicable, usuario inicial estable, persistencia al reabrir, precisión de importes y fechas, claves foráneas, revalidación al guardar, rollback inducido, cancelación previa y cuatro escritores concurrentes con conexiones independientes; también verifica que una misma clave con datos distintos produzca un solo ganador y un conflicto. Los archivos temporales se eliminan al terminar.

## Próximo paso

Exponer los casos de uso mediante endpoints financieros, adaptar requests y errores y verificar el recorrido desde un cliente HTTP. Preparar y comprobar copias de respaldo antes del despliegue en Raspberry.

Quedan pendientes los casos de uso de ingresos, transferencias, gestión de categorías, deudas, inversiones, proyecciones y edición o reversión, además del frontend. La persistencia implementa el modelo actual del core; todavía no incluye tipos de cuenta o fechas de creación de cuentas, campos conceptuales que no están presentes en ese modelo.
