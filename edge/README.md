# Edge · Laboratorio 3 · Fase 1

Servicio ASP.NET Core para recibir telemetría JSON v1 por HTTP/HTTPS y almacenarla en SQLite. Cada mensaje aceptado queda en estado `Pending`. El acuse confirma persistencia local en Edge. La confirmación definitiva en MariaDB corresponde a una fase posterior. [Interfaces comunes](https://github.com/FAJIFBEFJHA/lab-software-telecomunicaciones/issues/1) · [Alcance de Edge](https://github.com/FAJIFBEFJHA/lab-software-telecomunicaciones/issues/4).

## Requisitos e instalación

Usar el SDK .NET 10 LTS con el runtime ASP.NET Core. Restaurar los paquetes NuGet de Microsoft.Data.Sqlite y xUnit. Los comandos parten de `edge/`.

```powershell
dotnet --info
dotnet restore .\Edge.Tests\Edge.Tests.csproj
dotnet build .\Edge.Tests\Edge.Tests.csproj -c Release --no-restore
```

## Configuración local y ejecución

`Edge.Api/appsettings.json` contiene los valores públicos. Entregar las credenciales al proceso mediante variables de entorno u otro proveedor privado. Para desarrollo, aprovisionar una identidad de prueba, por ejemplo `virtual-01`, con un token nuevo de 32 bytes aleatorios codificados como 64 caracteres hexadecimales en minúscula. Usar el mismo token en el productor de prueba. El servicio exige al menos una identidad y rechaza tokens repetidos entre nodos.

| Configuración | Valor inicial | Función |
| --- | --- | --- |
| `Edge__Nodes__<node_id>` | Sin valores | Token privado por nodo. La identidad usa 1 a 64 letras ASCII, dígitos, guiones o guiones bajos. |
| `Edge__DatabasePath` | `.runtime/edge.sqlite` | Archivo SQLite. Las rutas relativas se resuelven desde la raíz de contenido del servicio. |
| `Edge__Capacity` | `10000` | Máximo de filas conservadas en SQLite. |
| `Edge__WriteTimeoutSeconds` | `3` | Espera por bloqueos y comandos de SQLite, en segundos. |
| `Urls` | `http://127.0.0.1:5080` | Escucha HTTP de prueba en loopback. |

En PowerShell, con `$edgeLocalToken` cargado desde una fuente privada:

```powershell
Set-Item -LiteralPath 'Env:Edge__Nodes__virtual-01' -Value $edgeLocalToken
dotnet run --project .\Edge.Api\Edge.Api.csproj -c Release --no-build
```

Edge lee el mapa al arrancar. Los cambios de credenciales requieren reiniciar el proceso. Usar un directorio de datos persistente y privado con permiso de escritura. Conservar SQLite y sus archivos WAL/SHM asociados según el procedimiento de SQLite. El repositorio excluye bases, artefactos de ejecución y archivos de claves.

## Solicitud y respuesta

Desde el cliente de prueba con el mismo token aprovisionado, enviar `POST /api/v1/telemetry` con `Content-Type: application/json` y `Authorization: Bearer <token del nodo>`.

```powershell
$edgeSample = @'
{"node_id":"virtual-01","seq":1,"timestamp":null,"value":1,"rssi":null,"extensions":{"pir_simulated":true,"pir_source":"virtual","config_version":0,"interval_ms":5000}}
'@
Invoke-WebRequest -UseBasicParsing -Method Post -Uri 'http://127.0.0.1:5080/api/v1/telemetry' -ContentType 'application/json' -Headers @{ Authorization = "Bearer $edgeLocalToken" } -Body $edgeSample
```

Para una clave nueva, el servidor responde **201** después del commit. Si repites un mensaje equivalente en la misma base, responde **200** y conserva la primera fila. Ambos devuelven este cuerpo:

```json
{"ok":true,"data":{"node_id":"virtual-01","seq":1,"state":"queued"},"meta":{"api_version":"v1"}}
```

Cambiar el valor de una muestra con la misma identidad y secuencia devuelve **409**:

```json
{"ok":false,"error":{"code":"sequence_conflict","message":"La secuencia ya existe con otro contenido.","details":{}},"meta":{"api_version":"v1"}}
```

| Estado | Causa |
| --- | --- |
| 400 | JSON malformado, UTF-8 inválido o profundidad de análisis excesiva. |
| 401 | Bearer ausente, con formato inválido o desconocido. |
| 403 | Identidad autenticada distinta de la del mensaje o HTTP desde fuera de loopback. |
| 409 | Contenido distinto para la misma clave. |
| 415 | Tipo de contenido distinto de JSON o charset explícito distinto de UTF-8. |
| 422 | Contrato v1 inválido o cuerpo mayor de 16 384 bytes. |
| 500 | Escritura SQLite rechazada por un error interno. |
| 503 | Cola llena, bloqueo agotado o almacenamiento sin espacio. |

## HTTPS con certificados confiables

Para las placas y el tráfico de red con credenciales, configurar un endpoint HTTPS en Kestrel. Aprovisionar un certificado de servidor con cadena confiable para el cliente. Incluir en el SAN el nombre DNS o la IP que usarán los productores y configurar en cada ESP32 la CA que corresponda. Conservar la validación de nombre y cadena en los clientes.

Ejemplo con un PFX privado y contraseña suministrada al proceso desde una fuente privada:

```powershell
$env:Kestrel__Endpoints__Telemetry__Url = 'https://0.0.0.0:5443'
$env:Kestrel__Endpoints__Telemetry__Certificate__Path = 'C:\certificados\edge.pfx'
$env:Kestrel__Endpoints__Telemetry__Certificate__Password = $edgeCertificatePassword
dotnet run --project .\Edge.Api\Edge.Api.csproj -c Release --no-build
```

`0.0.0.0` es la dirección de escucha. Los clientes usan el nombre o la IP del certificado. Los endpoints de Kestrel reemplazan `Urls`. Si se configura también HTTP, limitar su escucha a loopback. [Configuración de endpoints HTTPS de Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0).

## Persistencia y comparación

- La autenticación resuelve el nodo a partir del token antes de analizar el JSON. El `node_id` del cuerpo se contrasta con esa identidad. El mapa de búsqueda usa digests SHA-256 en memoria y el servicio mantiene los tokens fuera de SQLite y de los logs.
- SQLite usa WAL y configura `synchronous=FULL` en cada conexión de escritura. Una puerta asíncrona serializa las escrituras del proceso. La transacción inmediata de SQLite protege la comprobación de clave y capacidad frente a otros escritores. La clave primaria `(node_id, seq)` es el único índice requerido en esta fase. [Transacciones SQLite en .NET](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions) · [Bloqueos y tiempos de espera](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors).
- El JSON original se guarda como texto, con su orden, espacios y escapes. Para comparar se usan `timestamp`, `value`, `rssi` y `extensions` normalizados. Las claves de objetos se ordenan y las listas preservan el orden. `extensions` omitido equivale a `{}`. Los enteros conservan su precisión y `value` admite 0/1 con sintaxis entera o decimal. El límite de 4096 bytes de `extensions` reproduce la serialización PHP con escapes.
- Los campos de configuración de DEC-003 se conservan como datos de `extensions`. Sus valores aplicados se interpretarán en la fase de sincronización. La cola inicia `attempt_count` en cero y `next_retry_at` en el instante de recepción. Los tiempos internos son Unix en milisegundos.
- La capacidad cuenta todas las filas conservadas. Al agotarse, Edge rechaza claves nuevas con 503 y sigue reconociendo duplicados y conflictos de las filas existentes. Se conserva todo original aceptado.

## Pruebas

```powershell
dotnet test .\Edge.Tests\Edge.Tests.csproj -c Release --no-build --no-restore
```

Cada caso HTTP usa Kestrel en loopback con puerto efímero y una base SQLite aislada. Las pruebas cubren autenticación, acuses, validación, conservación exacta, concurrencia, saturación, bloqueo, escritura fallida y reinicio. Usan datos de prueba y limpian la base al finalizar.

Para comparar directamente con `TelemetryService::validate` de Lab 2, suministrar una ruta de PHP CLI de 64 bits. La prueba usa el código histórico en lectura y funciona sin MariaDB. Sin la variable, xUnit la omite y ejecuta las demás.

```powershell
$env:EDGE_PHP_EXECUTABLE = 'C:\xampp\php\php.exe'
dotnet test .\Edge.Tests\Edge.Tests.csproj -c Release --no-build --no-restore
```

## Mapa para la defensa

| Archivo | Clase o función | Qué explicar |
| --- | --- | --- |
| `Edge.Api/Program.cs` | `Program.Build`, `Main` | Configuración, dependencias, arranque de Kestrel e inicialización SQLite. |
| `Edge.Api/Configuration/EdgeSettings.cs` | `EdgeSettings.Load` | Ruta de base, capacidad y tiempo de espera. |
| `Edge.Api/Controllers/TelemetryController.cs` | `TelemetryController.Receive` | Autenticación, límite HTTP, validación y acuses. |
| `Edge.Api/Services/NodeCredentials.cs` | `NodeCredentials.Authenticate` | Resolución de la identidad confiable. |
| `Edge.Api/Services/TelemetryValidator.cs` | `TelemetryValidator.Validate` | Campos, tipos numéricos y límites JSON v1. |
| `Edge.Api/Services/ExtensionJson.cs` | `ExtensionJson.TryNormalize` | Tamaño y comparación de objetos, listas y números. |
| `Edge.Api/Models/TelemetryMessage.cs` y `TelemetryResponses.cs` | `TelemetryMessage`, `TelemetryContent`, `TelemetrySuccess`, `TelemetryFailure` | Original, comparación y respuesta v1. |
| `Edge.Api/Storage/SqliteTelemetryStore.cs` | `Initialize`, `AcceptAsync`, `Accept` | WAL, transacción, deduplicación, capacidad y commit. |
| `Edge.Tests/` | `EdgeTestHost` y clases `Telemetry*Tests` | Pruebas con Kestrel, SQLite y comparación con PHP. |

## Alcance actual

La fase termina con la aceptación local en `Pending`. La entrega, los lotes, el procesamiento por ventanas, la sincronización remota y las pruebas integrales T01–T07 quedan para fases posteriores. La comprobación de persistencia usa reinicio del servicio y una base aislada. La configuración HTTPS para placas requiere aprovisionar certificados confiables en el entorno de integración.
