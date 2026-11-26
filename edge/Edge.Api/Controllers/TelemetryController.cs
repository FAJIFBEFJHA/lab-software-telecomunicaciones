using System.Net;
using System.Text;
using System.Text.Json;
using Edge.Api.Models;
using Edge.Api.Services;
using Edge.Api.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Net.Http.Headers;

namespace Edge.Api.Controllers;

[ApiController]
[Route("api/v1/telemetry")]
public sealed class TelemetryController(
    NodeCredentials credentials,
    TelemetryValidator validator,
    SqliteTelemetryStore store,
    ILogger<TelemetryController> logger) : ControllerBase
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [HttpPost]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        if (!Request.IsHttps && !IsLoopback(HttpContext.Connection.RemoteIpAddress))
        {
            return Failure(403, "https_required", "Utilice HTTPS fuera de loopback.");
        }

        var authorization = Request.Headers.Authorization;
        var authenticatedNode = authorization.Count == 1 ? credentials.Authenticate(authorization[0]) : null;
        if (authenticatedNode is null)
        {
            Response.Headers.WWWAuthenticate = "Bearer";
            return Failure(401, "unauthorized", "Token ausente o invalido.");
        }

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType) ||
            !contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            (contentType.Charset.HasValue &&
                !string.Equals(contentType.Charset.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            return Failure(415, "unsupported_media_type", "Utilice application/json con codificacion UTF-8.");
        }

        const int maximumBytes = TelemetryValidator.MaximumBodyBytes;
        if (Request.ContentLength > maximumBytes)
        {
            return BodyTooLarge();
        }

        // A bounded read also enforces the limit for chunked requests.
        var body = new byte[maximumBytes + 1];
        var bytesRead = await Request.Body.ReadAtLeastAsync(body, body.Length, false, cancellationToken);
        if (bytesRead > maximumBytes)
        {
            return BodyTooLarge();
        }

        TelemetryMessage message;
        try
        {
            message = validator.Validate(StrictUtf8.GetString(body, 0, bytesRead));
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            return Failure(400, "malformed_json", "JSON malformado o demasiado profundo.");
        }
        catch (TelemetryValidationException error)
        {
            return Failure(422, "validation_error", error.Message, error.Details);
        }

        if (!string.Equals(authenticatedNode, message.NodeId, StringComparison.Ordinal))
        {
            return Failure(403, "identity_mismatch", "El token no pertenece al nodo solicitado.");
        }

        AcceptanceResult acceptance;
        try
        {
            acceptance = await store.AcceptAsync(message, cancellationToken);
        }
        catch (SqliteException error)
        {
            logger.LogWarning("SQLite rechazo telemetria de {NodeId} con secuencia {Sequence}. Codigo {SqliteCode}.",
                message.NodeId, message.Seq, error.SqliteErrorCode);
            return error.SqliteErrorCode is 5 or 6 or 13 // BUSY, LOCKED, FULL
                ? Failure(503, "storage_unavailable", "Almacenamiento temporalmente no disponible.")
                : Failure(500, "storage_error", "No se pudo persistir la telemetria.");
        }

        logger.LogDebug("Aceptacion de telemetria para {NodeId} y secuencia {Sequence}: {Acceptance}.",
            message.NodeId, message.Seq, acceptance);
        var receipt = new TelemetrySuccess(new TelemetryReceipt(message.NodeId, message.Seq));
        return acceptance switch
        {
            AcceptanceResult.Created => StatusCode(201, receipt),
            AcceptanceResult.Duplicate => Ok(receipt),
            AcceptanceResult.Conflict => Failure(409, "sequence_conflict", "La secuencia ya existe con otro contenido."),
            AcceptanceResult.CapacityExceeded => Failure(503, "queue_full", "Cola de Edge llena."),
            _ => throw new InvalidOperationException("Resultado de almacenamiento no reconocido.")
        };
    }

    private IActionResult BodyTooLarge() =>
        Failure(422, "validation_error", "Cuerpo excede 16384 bytes.");

    private IActionResult Failure(int status, string code, string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        StatusCode(status, new TelemetryFailure(new ApiError(code, message, details ?? new Dictionary<string, string>())));

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
