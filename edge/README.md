# Edge · Laboratorio 3 · Fase 2

Edge recibe telemetría JSON v1 por HTTP/HTTPS y confirma su persistencia local en SQLite. Un worker opcional entrega los originales al backend en modo individual o por lotes. La confirmación definitiva se registra cuando Edge valida el acuse del backend y guarda el resultado en SQLite. [Interfaces comunes](https://github.com/FAJIFBEFJHA/lab-software-telecomunicaciones/issues/1) · [Alcance de Edge](https://github.com/FAJIFBEFJHA/lab-software-telecomunicaciones/issues/4).

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
| `Edge__Delivery__Enabled` | `false` | Habilita el reenvío. La recuperación de filas interrumpidas también se hace al arrancar con el reenvío desactivado. |
| `Edge__Delivery__BackendUrl` | Vacío | Origen obligatorio al habilitar el reenvío. Usar HTTPS o HTTP en loopback, sin ruta adicional, credenciales, parámetros ni fragmentos. |
| `Edge__Delivery__TimeoutMilliseconds` | `5000` | Plazo para completar la solicitud y leer el acuse. |
| `Edge__Delivery__PollMilliseconds` | `500` | Intervalo de consulta cuando no hay muestras listas o el worker necesita recuperarse de un error inesperado. |
| `Edge__Delivery__Mode` | `Individual` | `Individual` o `Batch`. Reiniciar Edge para cambiar de modo. |
| `Edge__Delivery__BatchSize` | `10` | Cantidad máxima de muestras por lote, de 1 a 10. |
| `Edge__Delivery__BatchWaitMilliseconds` | `2000` | Umbral de espera de una muestra elegible para enviar un lote incompleto. Admite 0. |
| `Edge__Delivery__MaxJitterMilliseconds` | `250` | Máximo jitter adicional por reintento, entre 0 y 60 000 ms. Cero lo desactiva. |

En PowerShell, con `$edgeLocalToken` cargado desde una fuente privada:

```powershell
Set-Item -LiteralPath 'Env:Edge__Nodes__virtual-01' -Value $edgeLocalToken
dotnet run --project .\Edge.Api\Edge.Api.csproj -c Release --no-build
```

Edge lee el mapa al arrancar. Para rotar un token, aprovisionar el valor nuevo en el backend, el productor y el mapa privado de Edge. Reiniciar Edge para aplicar cambios o revocaciones. Las filas conservadas se identifican por nodo y secuencia. Los intentos nuevos usan el mapa cargado al arrancar.

Usar un directorio de datos persistente y privado con permiso de escritura. Conservar SQLite y sus archivos WAL/SHM asociados según el procedimiento de SQLite. **Ejecutar una sola instancia de Edge por archivo SQLite.** La recuperación devuelve a la cola todos los envíos interrumpidos de esa instancia.

### Demostración independiente con backend simulado

Después de compilar, iniciar el simulador en una terminal desde `edge/`. Los argumentos son el puerto de loopback y el código de respuesta del fixture. El simulador atiende ambos endpoints con HTTP, y genera los acuses a partir de las identidades y secuencias recibidas.

```powershell
dotnet run --project .\Edge.Tests\Edge.Tests.csproj -c Release --no-build -- 5090 201
```

En una segunda terminal desde `edge/`, con `$edgeLocalToken` cargado desde una fuente privada de pruebas, iniciar Edge con una base separada para la demostración.

```powershell
Set-Item -LiteralPath 'Env:Edge__Nodes__virtual-01' -Value $edgeLocalToken
$env:Edge__DatabasePath = '.runtime/demo-entrega.sqlite'
$env:Edge__Delivery__Enabled = 'true'
$env:Edge__Delivery__BackendUrl = 'http://127.0.0.1:5090'
$env:Edge__Delivery__Mode = 'Individual'
dotnet run --project .\Edge.Api\Edge.Api.csproj -c Release --no-build
```

Desde otra terminal, usar el mismo token de pruebas y la solicitud de ejemplo de la sección siguiente. Para probar lotes, detener Edge, cambiar el modo a `Batch`, volver a iniciarlo y enviar secuencias nuevas. Con una sola muestra se puede observar la espera del lote incompleto. Para observar recuperación, ejecutar el simulador con `5090 503`, enviar secuencias nuevas y luego reiniciar solo el simulador con `5090 200`. Edge reintenta en la fecha guardada en SQLite. Los códigos configurados en esta demostración son respuestas de prueba. Las pruebas automatizadas programan fallos por solicitud, y modelan la deduplicación tras pérdida de acuse con un registro en memoria.

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

- La autenticación resuelve el nodo a partir del token antes de analizar el JSON. El `node_id` del cuerpo se contrasta con esa identidad. `NodeCredentials` mantiene en memoria los digests SHA-256 para autenticar y el token privado por nodo para el Bearer de reenvío. Edge deja las credenciales fuera de SQLite, del JSON reenviado y de los logs.
- SQLite usa WAL y configura `synchronous=FULL` en cada conexión de escritura. Una puerta asíncrona y transacciones inmediatas coordinan la ingesta y la entrega. La clave primaria `(node_id, seq)` conserva la unicidad. Un índice parcial ayuda a elegir pendientes por identidad y fecha de reintento. La tabla de Fase 1 es compatible y se conserva al arrancar. [Transacciones SQLite en .NET](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions) · [Bloqueos y tiempos de espera](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors).
- El JSON original se guarda como texto, con su orden, espacios y escapes. Para comparar se usan `timestamp`, `value`, `rssi` y `extensions` normalizados. Las claves de objetos se ordenan y las listas preservan el orden. `extensions` omitido equivale a `{}`. Los enteros conservan su precisión y `value` admite 0/1 con sintaxis entera o decimal. El límite de 4096 bytes de `extensions` reproduce la serialización PHP con escapes.
- Los campos de configuración de DEC-003 se conservan como datos de `extensions`. Sus valores aplicados se interpretarán en la fase de sincronización. La cola inicia `attempt_count` en cero y `next_retry_at` en el instante de recepción. Los tiempos internos son Unix en milisegundos.
- La capacidad cuenta todas las filas conservadas, incluidas `Delivered` y `Failed`. Al agotarse, Edge rechaza claves nuevas con 503 y sigue reconociendo duplicados y conflictos de las filas existentes. Aumentar la capacidad permite aceptar nuevas claves a costa del espacio en disco. La retención es indefinida en esta fase.

## Ciclo de entrega

| Transición | Escritura en SQLite |
| --- | --- |
| Recepción → `Pending` | Guarda el original y su fecha. Inicializa `attempt_count` a 0 y `next_retry_at` a la fecha de recepción. |
| `Pending` → `Sending` | Selecciona filas elegibles por `next_retry_at`, incrementa `attempt_count` por muestra y confirma la transacción antes del POST. |
| `Sending` → `Delivered` | Guarda `delivered_at` y limpia `last_error` tras validar el acuse definitivo. |
| `Sending` → `Pending` | Programa `next_retry_at` y guarda un código de diagnóstico para el fallo transitorio. |
| `Sending` → `Failed` | Conserva el original y un código de diagnóstico por un fallo permanente. Estas filas quedan fuera de los reintentos automáticos. |
| `Sending` → `Pending` por interrupción | Al arrancar, o tras una cancelación o un fallo inesperado del worker, conserva el contador y registra `interrupted`. El mensaje vuelve a ser elegible. |

El worker hace una solicitud a la vez y rota entre identidades con muestras listas. Dentro de cada identidad elige por fecha de elegibilidad, recepción y secuencia. Durante el intercambio HTTP, la ingesta mantiene acceso a SQLite. Un backend que deja de responder puede ocupar el worker hasta el timeout y los demás nodos vuelven a competir en el turno siguiente. Si Edge pierde un acuse o se detiene antes de guardarlo, el backend puede recibir de nuevo el mismo original. La idempotencia depende de la clave `(node_id, seq)` y del 200 ante un duplicado idéntico persistido.

### Modos y acuses

| Modo | Solicitud | Criterio de entrega |
| --- | --- | --- |
| Individual | `POST /api/v1/telemetry` con el JSON original. | HTTP 200 o 201, `ok: true`, `meta.api_version: "v1"` y `data.node_id` y `data.seq` iguales a lo enviado. |
| Lote | `POST /api/v1/telemetry/batch` con `{"messages":[...]}`. Inserta los originales de una sola identidad directamente en el arreglo. | HTTP 200, la envoltura v1 exitosa y `data.node_id` correspondiente. Cada resultado de `data.results` se valida por índice desde 0, secuencia y estado. Solo 200 o 201 por entrada acreditan entrega. |

Edge usa el Bearer del nodo y la validación TLS normal de .NET. Las solicitudes HTTP de prueba conectan directamente a loopback. Las redirecciones quedan deshabilitadas. Cada muestra se limita a 16 384 bytes al ingresar y el cuerpo del lote a 256 KiB. El máximo de 10 muestras de tamaño válido cabe dentro de ese límite aun con la envoltura. El worker envía cuando hay suficientes muestras elegibles del nodo o cuando la más antigua alcanza la espera configurada desde `next_retry_at`. Al detectar un lote incompleto, adelanta el siguiente sondeo si vence ese plazo antes del próximo polling. La detección inicial depende del polling y de la disponibilidad del único worker.

En un lote se persisten juntos los resultados válidos. Las entradas ausentes, con un estado desconocido o con índice o secuencia incorrectos o ambiguos, vuelven a la cola. Un índice repetido impide confirmar esa posición y una secuencia repetida impide confirmar las entradas que la reclaman. Se aceptan acuses de hasta 64 KiB.

### Reintentos y diagnóstico

| Resultado | Tratamiento |
| --- | --- |
| Timeout, error de conexión, HTTP 408, 429 o 5xx | Reintento con el original. |
| Acuse sin confirmación válida u otro 2xx global fuera del contrato | Reintento de las muestras afectadas. |
| HTTP 400, 401, 403, 409 o 422, otros 4xx y redirecciones 3xx | Fallo permanente con el estado HTTP en `last_error`. Otros estados finales 1xx o fuera del rango esperado también quedan como fallos permanentes. |
| Resultado válido de lote 409 o 422 | Fallo permanente solo de esa entrada. |
| Resultado válido de lote 500 o 503 | Reintento solo de esa entrada. |
| Identidad sin credencial de salida | Fallo permanente con `missing_node_credentials`. |

El backoff base por muestra es 1, 2, 4, 8, 16, 32 y 60 segundos. Desde el séptimo intento la base permanece en 60 segundos. A cada espera se suma jitter aleatorio entre 0 y el máximo configurado. Un `Retry-After` válido de una respuesta, en segundos o fecha HTTP, fija un límite inferior para sus reintentos. Los valores inválidos o vencidos no alargan la espera. El contador persiste tras reinicios y no hay tope de intentos. `last_error` contiene códigos generados por Edge, sin copiar encabezados, cuerpos ni mensajes de excepción remotos.

### Observabilidad

Los logs correlacionan identidad, secuencia, número de intento, confirmación, fallo y fecha del siguiente reintento. Por solicitud registran el modo, la cantidad de muestras y los bytes UTF-8 del cuerpo HTTP preparado. Esa métrica incluye la envoltura en modo lote y se registra para solicitudes intentadas, incluso si la conexión falla antes de transmitir el cuerpo. Excluye encabezados, respuestas, TLS y demás bytes de red. La latencia registrada al confirmar va desde `received_at` hasta `delivered_at` con el reloj UTC de Edge. Incluye la espera en cola y los reintentos.

## Pruebas

La suite inicia Kestrel en puertos efímeros de loopback y usa una base SQLite real aislada por caso. `SimulatedTelemetryBackend.StartAsync` recibe una función por solicitud para devolver estados y acuses, bloquear una respuesta o cerrar la conexión. Los casos de entrega fijan el jitter a cero donde necesitan verificar un plazo exacto, y avanzan un reloj controlado para los reintentos y la formación de lotes.

```powershell
dotnet test .\Edge.Tests\Edge.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~TelemetryDeliveryTests
```

Para ejecutar la suite completa, incluir PHP CLI de 64 bits. La prueba de comparación ejecuta `TelemetryService::validate` de Lab 2 en modo de lectura y funciona sin MariaDB. xUnit omite ese caso si falta la variable.

```powershell
$env:EDGE_PHP_EXECUTABLE = 'C:\xampp\php\php.exe'
dotnet test .\Edge.Tests\Edge.Tests.csproj -c Release --no-build --no-restore
```

Resultado local con .NET SDK 10.0.401 y PHP CLI 8.2.12 x64 en Windows. Release compiló con **0 advertencias y 0 errores**. Pasaron **92 pruebas**, incluidas las 50 anteriores y los 34 casos contrastados con PHP dentro de una de ellas. Las 42 pruebas nuevas cubren entrega y acuses individuales, respuestas parciales y ambiguas, reintentos, pérdida de conexión y de acuse, recuperación y cancelación, conservación exacta, concurrencia, aislamiento entre nodos, HTTPS como origen configurable y protección de los tokens.

La demostración con Edge y el simulador en procesos separados confirmó una muestra al primer intento en cada modo. Para esa muestra, Edge registró 71 bytes de cuerpo en modo individual y 86 bytes en un lote de una muestra, incluida su envoltura.

## Mapa para la defensa

| Archivo | Clase o función | Qué explicar |
| --- | --- | --- |
| `Edge.Api/Program.cs` | `Program.Build`, `Main` | Configuración, dependencias, registro de HttpClient y el worker, arranque de Kestrel e inicialización SQLite. |
| `Edge.Api/Configuration/EdgeSettings.cs` | `EdgeSettings.Load`, `DeliverySettings.Load` | Ruta SQLite, capacidad, origen del backend, modo y límites de entrega. |
| `Edge.Api/Controllers/TelemetryController.cs` | `TelemetryController.Receive` | Autenticación, límite HTTP, validación y acuses. |
| `Edge.Api/Services/NodeCredentials.cs` | `Authenticate`, `GetDeliveryToken` | Resolución de la identidad y acceso privado al Bearer del nodo. |
| `Edge.Api/Services/TelemetryValidator.cs` | `TelemetryValidator.Validate` | Campos, tipos numéricos y límites JSON v1. |
| `Edge.Api/Services/ExtensionJson.cs` | `ExtensionJson.TryNormalize` | Tamaño y comparación de objetos, listas y números. |
| `Edge.Api/Models/TelemetryMessage.cs` y `TelemetryResponses.cs` | `TelemetryMessage`, `TelemetryContent`, `TelemetrySuccess`, `TelemetryFailure` | Original, comparación y respuesta v1. |
| `Edge.Api/Models/TelemetryDelivery.cs` | `DeliveryAttempt`, `DeliveryResult`, `DeliveryUpdate` | Datos del intento, resultado validado y siguiente fecha de reintento. |
| `Edge.Api/Storage/SqliteTelemetryStore.cs` | `Initialize`, `AcceptAsync`, `WriteAsync` | WAL, deduplicación, capacidad y transacciones de escritura compartidas. |
| `Edge.Api/Storage/SqliteTelemetryStore.cs` | `ClaimAsync`, `CompleteAsync`, `RecoverSendingAsync` | Selección y registro de intentos, resultados y recuperación. |
| `Edge.Api/Services/TelemetryDeliveryWorker.cs` | `ExecuteAsync`, `DeliverAsync`, `LogResults` | Turnos por nodo, reintentos, cancelación y métricas por muestra. |
| `Edge.Api/Services/BackendTelemetryClient.cs` | `CreateHandler`, `SendAsync`, `ReadBatchResults` | Transporte, timeout, conservación de originales y validación de acuses. |
| `Edge.Tests/` | `EdgeTestHost`, `SimulatedTelemetryBackend` y clases `Telemetry*Tests` | Demostración manual, pruebas con Kestrel y SQLite, y comparación con PHP. |

## Límites de la Fase 2

La evidencia local cubre Edge y su backend simulado. La integración con placas, MariaDB y Digital Twin, el procesamiento por ventanas, la sincronización remota y la ejecución integral de T01–T07 quedan pendientes. La instrumentación registra eventos de aplicación para una futura comparación de modos y una medición de tráfico de red. La prueba de reinicio usa una fila `Sending` preparada en SQLite para representar un proceso interrumpido. Un caso distinto comprueba la cancelación ordenada de una solicitud en curso.
