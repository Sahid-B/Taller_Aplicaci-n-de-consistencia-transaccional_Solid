using BankingApp.Domain;

namespace BankingApp.Services;

// ================ Aqui se aplico SOLID D: negocio depende de contratos, no de PostgreSQL o HTTP ================
public sealed class TransferService(ITransferRepository repository, ITransferAuthorizer authorizer,
    IEnumerable<ITransferObserver> observers, ILogger<TransferService> logger) : ITransferService
{
    public async Task<TransferResult> TransferAsync(TransferRequest request, string actor)
    {
        if (request.FromAccountId == request.ToAccountId || request.FromAccountId <= 0 || request.ToAccountId <= 0)
            throw new BusinessException("Seleccione dos cuentas diferentes");
        if (request.Amount <= 0 || request.Amount != decimal.Round(request.Amount, 2) || request.Amount > 9999999999999.99m)
            throw new BusinessException("El monto debe ser positivo y tener como maximo dos decimales");
        // Servicio externo solo valida: NO realiza movimientos monetarios fuera de PostgreSQL.
        await authorizer.AuthorizeAsync(request);
        var id = await repository.ExecuteAsync(request);
        var transfer = new TransferEvent(id, request.FromAccountId, request.ToAccountId, request.Amount);
        var results = new List<string>();
        // ================ Aqui se aplico Observer y SOLID O: agregar observadores sin cambiar el flujo ================
        // Se publica DESPUES de COMMIT: un fallo al notificar no revierte ni repite el dinero.
        foreach (var observer in observers)
        {
            try { results.Add(await observer.OnConfirmedAsync(transfer)); }
            catch (Exception ex) { logger.LogError(ex, "Observer fallo para transferencia confirmada {Id}", id); results.Add("Advertencia: un observador fallo; la transferencia ya esta confirmada"); }
        }
        return new(id, "COMMIT", results);
    }
}

// ================ Aqui se aplico Proxy: cliente HTTP -> TransferProxy -> TransferService real ================
public sealed class TransferProxy(TransferService realService, ILogger<TransferProxy> logger) : ITransferService
{
    public Task<TransferResult> TransferAsync(TransferRequest request, string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 80) throw new BusinessException("Identifique al operador (1 a 80 caracteres)");
        logger.LogInformation("PROXY: operador {Actor}, fecha {Date}, cuenta {Account}", actor, DateTimeOffset.UtcNow, request.FromAccountId);
        return realService.TransferAsync(request, actor);
    }
}

// ================ Aqui se aplico SOLID L: ambos observadores cumplen el mismo contrato intercambiable ================
public sealed class AuditObserver(ILogger<AuditObserver> logger) : ITransferObserver
{
    public Task<string> OnConfirmedAsync(TransferEvent transfer)
    {
        logger.LogInformation("OBSERVER AUDITORIA: transferencia {Id}, monto {Amount}", transfer.TransactionId, transfer.Amount);
        return Task.FromResult($"Auditoria: movimiento #{transfer.TransactionId} registrado en logs");
    }
}
public sealed class NotificationObserver(ILogger<NotificationObserver> logger) : ITransferObserver
{
    public Task<string> OnConfirmedAsync(TransferEvent transfer)
    {
        logger.LogInformation("OBSERVER NOTIFICACION: cuenta {Account} recibio {Amount}", transfer.ToAccountId, transfer.Amount);
        return Task.FromResult($"Notificacion: cuenta {transfer.ToAccountId} recibio {transfer.Amount:0.00}");
    }
}
