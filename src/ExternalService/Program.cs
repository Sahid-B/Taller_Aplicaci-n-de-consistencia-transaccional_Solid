var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var failing = 0;
var calls = 0;
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/status", () => new { failing = Volatile.Read(ref failing) == 1, calls = Volatile.Read(ref calls) });
// ================ Aqui se simulan fallos del proveedor externo para Circuit Breaker ================
app.MapPost("/mode/{mode}", (bool mode) => { Interlocked.Exchange(ref failing, mode ? 1 : 0); return Results.Ok(new { failing = mode }); });
// Interfaz del proveedor diferente de ITransferAuthorizer.AuthorizeAsync de nuestra aplicacion.
app.MapPost("/executeTransaction", (ProviderRequest request) =>
{
    Interlocked.Increment(ref calls);
    if (Volatile.Read(ref failing) == 1) return Results.Json(new { error = "Proveedor temporalmente fuera de servicio" }, statusCode: 503);
    if (request.Value <= 0 || string.IsNullOrWhiteSpace(request.Code)) return Results.BadRequest();
    return Results.Ok(new { accepted = true, reference = Guid.NewGuid().ToString("N") });
});
app.Run();
record ProviderRequest(decimal Value, string Code);
