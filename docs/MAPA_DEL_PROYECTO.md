# Mapa del proyecto — Software de Telecomunicaciones

**Actualizado:** 19/11/2026. **Alcance:** estructura versionada del Laboratorio 2; no constituye una nueva prueba ni una certificación del estado actual de los servicios.

## Qué copia contiene qué

- **GitHub** (`FAJIFBEFJHA/lab-software-telecomunicaciones`): código versionado, scripts de pruebas y documentación. No contiene necesariamente las salidas experimentales locales.
- **PC de escritorio:** copia clonada en `D:\Proyectos\lab-software-telecomunicaciones` según comprobación del usuario. No se ha acreditado la presencia de evidencias experimentales locales.
- **Laptop:** equipo usado para las pruebas físicas de Laboratorio 2, con configuraciones y evidencias locales. La ubicación y el inventario actuales de esos archivos **no se han auditado** desde este repositorio. Las rutas antiguas `C:\Proyecto\lab2-software-telecomunicaciones` aparecen en instrucciones y registros históricos; no son una ruta universal ni se sustituyen retroactivamente.

El cambio del nombre del repositorio no altera por sí solo los nombres internos `Lab2`, la base de datos `lab2`, los identificadores de los ESP32 ni el historial de las campañas.

## Qué significa cada carpeta

| Ruta | Función | Ejemplos |
| --- | --- | --- |
| `public/` | Punto de entrada web HTTP | `public/index.php` |
| `src/` | Código fuente del **backend PHP**, no firmware | `ApiController.php`, `TelemetryService.php`, `Database.php` |
| `firmware/` | Código Arduino/C++ destinado a las placas ESP32 | `esp32_01_http/`, `esp32_02_http/`, `esp32_03_mqtt/` |
| `tests/` | Código que **ejecuta o verifica pruebas**; no es por definición su resultado | `c1-http.php`, `d2-security.php`, `test_p14_evidence.py` |
| `scripts/` | Arranque, aprovisionamiento, captura y campañas de verificación; algunas herramientas también realizan pruebas | `start-lab2.ps1`, `capture-serial.py`, `verify-t05.php` |
| `sql/` | Esquema de base de datos y comprobación de conexión | `schema.sql`, `check-connection.php` |
| `config/` | Ejemplos públicos y configuración local excluida de Git | `database.example.php`; archivos `*.local.php` solo locales |
| `docs/` | Procedimientos, informes y análisis de resultados | `P14_RESULTADOS.md`, `P15_RESULTADOS.md` |
| `logs/` | Evidencia **generada localmente**, normalmente ignorada por Git | Capturas serie, resultados T05 y LOAD, latencia |
| `.runtime/` | Estado y registros de los servicios ejecutados localmente | `.runtime/start-lab2/` |

Un archivo `tests/*.php` puede crear datos en MariaDB; su presencia en el repositorio **no acredita** que la prueba se haya ejecutado. Un resumen en `docs/` describe una ejecución histórica y debe contrastarse con sus fuentes cuando se requiera acreditación.

## Recorrido de la telemetría

- **REST/HTTPS:** firmware del ESP32 → Apache (TLS 8443, configurado fuera del repositorio) → `public/index.php` → `src/ApiController.php` y `src/TelemetryController.php` → `src/TelemetryService.php` → repositorios PHP y MariaDB.
- **MQTT/TLS:** firmware MQTT → Mosquitto (TLS 8883) → `scripts/mqtt-subscriber.php` → `src/TelemetryService.php` → repositorios PHP y MariaDB.
- El servicio compartido implementa la lógica de telemetría de ambos transportes; la autenticación y el protocolo de entrada no son idénticos.

## Dónde buscar resultados históricos

Estas rutas proceden del código o la documentación **como ubicaciones previstas**. No garantizan que los archivos existan hoy en cada computadora.

| Qué buscar | Ruta relativa de origen | Archivo o documento guía |
| --- | --- | --- |
| Capturas serie | `logs/serial/<campaña>/` | `scripts/capture-serial.py` |
| Recuperación P14 | `logs/evidence/P14/<ejecución>/` | `docs/P14_PROCEDIMIENTO.md`, `docs/P14_RESULTADOS.md` |
| Verificación retrospectiva T05 | `logs/evidence/T05/<ejecución>/` | `scripts/verify-t05.php`; produce `summary.md` y `evidence.json` |
| Prueba de carga HTTP | `logs/evidence/LOAD/<campaña>/` | `scripts/http-load.php`; produce `requests.csv`, `summary.csv`, `summary.md` |
| Mediciones de latencia interna | `logs/latency/<campaña>/` | `scripts/p15-latency.ps1`, `docs/P15_RESULTADOS.md` |
| Fuentes o paquetes de P15 | `logs/evidencias-p15/` cuando existan | `docs/P15_RESULTADOS.md`, sección «Archivos y procedimiento de reproducción» |
| Logs de arranque de servicios | `.runtime/start-lab2/` | `scripts/start-lab2.ps1` |
| Logs de HTTPS de Apache en la laptop | Bajo la instalación local de XAMPP | Configuración local de Apache; no son archivos versionados |

**Distinción de resultados:** P15 registra una campaña física simultánea; T05 contrasta retrospectivamente sus 336 claves. No son dos campañas físicas diferentes. P14-B/C conservan la calificación **INCOMPLETO**, aunque se observara recuperación funcional. El LOAD HTTP local no mide el rendimiento extremo a extremo de los ESP32.

## Cómo comprobar una evidencia sin repetir una prueba

1. Identificar el requisito solicitado y el documento de resultados que lo describe.
2. Buscar la ruta de la **fuente original** indicada en ese documento; comprobar su existencia en la laptop, no asumir que GitHub la contiene.
3. Identificar el script que la generó o analizó. Distinguir un script de pruebas de la salida de una ejecución anterior.
4. Correlacionar la fuente, el resultado y sus limitaciones. No convertir un `INCOMPLETO` en `PASS`.
5. Presentar únicamente información necesaria y desprovista de credenciales, tokens y claves privadas. No subir la totalidad de `logs/` o `config/` a GitHub.

Para enumerar carpetas **sin ejecutar pruebas ni modificar archivos**, desde la raíz local del proyecto:

```powershell
Get-ChildItem .\logs -Directory -ErrorAction SilentlyContinue |
    Select-Object Name, FullName
Get-ChildItem .\logs\evidence -Directory -ErrorAction SilentlyContinue |
    Select-Object Name, FullName
```

Si no existe `logs/` en la PC, eso no significa que las pruebas históricas no se hayan realizado: la fuente puede seguir en la laptop.

## Antes del siguiente laboratorio

Este mapa describe **Laboratorio 2**. Los requisitos futuros —telemetría simulada sin PIR/relé y registro de nodos físicos o virtuales— todavía no están implementados ni acreditados por este documento. No modificar los resultados históricos para representar esos requisitos.
