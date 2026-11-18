using BankingApp.Domain;

namespace BankingApp.Services;

// ================ Aqui se aplico Circuit Breaker: CLOSED -> OPEN -> HALF-OPEN -> CLOSED ================
// Singleton: el estado se comparte entre solicitudes. Solo pasa una llamada de prueba en HALF-OPEN.
public sealed class CircuitBreaker(ILogger<CircuitBreaker> logger)
{
    private readonly object gate = new();
    private int failures;
    private string state = "CLOSED";
    private DateTimeOffset? retryAt;
    private bool probing;
    private int generation;
    private readonly List<string> history = [];
    public object Snapshot() { lock (gate) return new { state, failures, retryAt, history = history.ToArray(), threshold = 3, cooldownSeconds = 10 }; }
    private void Transition(string next)
    {
        var entry = $"{DateTimeOffset.UtcNow:HH:mm:ss} {state} -> {next}";
        history.Add(entry); if (history.Count > 30) history.RemoveAt(0);
        logger.LogWarning("CIRCUIT BREAKER: {Transition}", entry); state = next; generation++;
    }
    public async Task ExecuteAsync(Func<Task> action)
    {
        int attemptGeneration;
        lock (gate)
        {
            if (state == "OPEN")
            {
                if (DateTimeOffset.UtcNow < retryAt) throw new DependencyException("Circuit Breaker OPEN: no se envio ninguna solicitud al proveedor; espere 10 segundos");
                Transition("HALF-OPEN");
            }
            if (state == "HALF-OPEN")
            {
                if (probing) throw new DependencyException("Circuit Breaker HALF-OPEN: ya existe una llamada de prueba");
                probing = true;
            }
            attemptGeneration = generation;
        }
        try
        {
            await action();
            lock (gate)
            {
                // Una llamada previa en vuelo no debe cerrar un circuito que otra acaba de abrir.
                if (generation == attemptGeneration)
                {
                    if (state == "HALF-OPEN") { failures = 0; probing = false; retryAt = null; Transition("CLOSED"); }
                    else if (state == "CLOSED") failures = 0;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DependencyException)
        {
            lock (gate)
            {
                if (generation == attemptGeneration)
                {
                    failures++;
                    if (state == "HALF-OPEN" || state == "CLOSED" && failures >= 3)
                    { probing = false; retryAt = DateTimeOffset.UtcNow.AddSeconds(10); Transition("OPEN"); }
                }
            }
            throw new DependencyException("Proveedor externo fallo; la transferencia no se ejecuto");
        }
    }
}

// ================ Aqui se aplico Adapter: AuthorizeAsync -> executeTransaction(value, code) ================
public sealed class ProviderAdapter(IHttpClientFactory clients, CircuitBreaker circuit, ILogger<ProviderAdapter> logger) : ITransferAuthorizer
{
    public Task AuthorizeAsync(TransferRequest request) => circuit.ExecuteAsync(async () =>
    {
        logger.LogInformation("ADAPTER: transforma Amount -> value y cuentas -> code");
        using var response = await clients.CreateClient("provider").PostAsJsonAsync("executeTransaction", new { value = request.Amount, code = $"{request.FromAccountId}:{request.ToAccountId}" });
        response.EnsureSuccessStatusCode();
    });
}
