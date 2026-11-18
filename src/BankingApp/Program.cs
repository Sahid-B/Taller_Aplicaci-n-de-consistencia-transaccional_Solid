using BankingApp.Data;
using BankingApp.Domain;
using BankingApp.Services;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
// ================ Aqui se aplico Inversion de Dependencias mediante inyeccion de contratos ================
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Banking")
    ?? throw new InvalidOperationException("Falta ConnectionStrings__Banking")));
builder.Services.AddSingleton<BankRepository>();
builder.Services.AddSingleton<ICustomerRepository>(sp => sp.GetRequiredService<BankRepository>());
builder.Services.AddSingleton<IBankQueries>(sp => sp.GetRequiredService<BankRepository>());
builder.Services.AddSingleton<ITransferRepository>(sp => sp.GetRequiredService<BankRepository>());
builder.Services.AddSingleton<CircuitBreaker>();
builder.Services.AddHttpClient("provider", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ExternalServiceUrl"] ?? "http://external-service:8080/");
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddSingleton<ITransferAuthorizer, ProviderAdapter>();
builder.Services.AddSingleton<ITransferObserver, AuditObserver>();
builder.Services.AddSingleton<ITransferObserver, NotificationObserver>();
builder.Services.AddSingleton<TransferService>();
builder.Services.AddSingleton<ITransferService, TransferProxy>();
var app = builder.Build();
// ================ Aqui se aplico manejo controlado de errores sin exponer credenciales ================
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception ex)
    {
        var (status, message) = ex switch
        {
            BusinessException => (400, ex.Message),
            DependencyException => (503, ex.Message),
            PostgresException pg when pg.SqlState == "23505" => (409, "Ya existe un registro con ese correo o identificador"),
            PostgresException pg when pg.SqlState is "23503" or "23514" or "22001" or "22003" => (400, "El dato viola una relacion, restriccion o longitud permitida"),
            BadHttpRequestException => (400, "Revise el formato de los datos enviados"),
            _ => (500, "No se pudo completar la operacion. Revise los logs del servidor")
        };
        context.Response.StatusCode = status;
        app.Logger.LogWarning("Operacion rechazada: {Type}", ex.GetType().Name);
        if (status == 500) app.Logger.LogError(ex, "Error inesperado");
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", async (NpgsqlDataSource source) => { await using var cmd = source.CreateCommand("SELECT 1"); await cmd.ExecuteScalarAsync(); return Results.Ok(new { status = "ok" }); });
app.MapGet("/api/dashboard", (IBankQueries queries) => queries.DashboardAsync());
app.MapGet("/api/customers", (ICustomerRepository repository) => repository.ListAsync());
app.MapPost("/api/customers", async (CustomerInput input, ICustomerRepository repository) =>
{ ValidateCustomer(input); var id = await repository.CreateAsync(input); return Results.Created($"/api/customers/{id}", new { id }); });
app.MapPut("/api/customers/{id:int}", async (int id, CustomerInput input, ICustomerRepository repository) =>
{ ValidateCustomer(input); await repository.UpdateAsync(id, input); return Results.Ok(new { message = "Cliente actualizado" }); });
app.MapDelete("/api/customers/{id:int}", async (int id, ICustomerRepository repository) =>
{ await repository.DeleteAsync(id); return Results.Ok(new { message = "Cliente eliminado logicamente; historial conservado" }); });
app.MapGet("/api/accounts", (IBankQueries queries) => queries.AccountsAsync());
app.MapGet("/api/transactions", (IBankQueries queries) => queries.TransactionsAsync());
app.MapGet("/api/loans", (IBankQueries queries) => queries.LoansAsync());
app.MapPost("/api/transfers", (TransferRequest request, HttpContext context, ITransferService service) =>
    service.TransferAsync(request, context.Request.Headers["X-Operator"].ToString()));
app.MapGet("/api/circuit", (CircuitBreaker circuit) => circuit.Snapshot());
app.MapGet("/api/provider", async (IHttpClientFactory clients) =>
{ using var response = await clients.CreateClient("provider").GetAsync("status"); response.EnsureSuccessStatusCode(); return Results.Content(await response.Content.ReadAsStringAsync(), "application/json"); });
app.MapPost("/api/provider/{failing:bool}", async (bool failing, IHttpClientFactory clients) =>
{ using var response = await clients.CreateClient("provider").PostAsync($"mode/{failing.ToString().ToLowerInvariant()}", null); response.EnsureSuccessStatusCode(); return Results.Ok(new { failing }); });

// ================ Aqui se demostro Concurrencia: dos conexiones compiten por el mismo saldo ================
app.MapPost("/api/demo/concurrency", async (NpgsqlDataSource source, ITransferService service) =>
{
    await using var connection = await source.OpenConnectionAsync();
    await using var setup = await connection.BeginTransactionAsync();
    await using var customer = new NpgsqlCommand("INSERT INTO customer(first_name,last_name,email,city) VALUES('Demo','Concurrencia',$1,'Quito') RETURNING customer_id", connection, setup);
    customer.Parameters.AddWithValue($"demo-{Guid.NewGuid():N}@example.com");
    var customerId = (int)(await customer.ExecuteScalarAsync())!;
    var ids = new List<int>();
    for (var i = 0; i < 3; i++)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO account(account_number,customer_id,branch_id,account_type,opening_balance,balance,status,opened_date) VALUES($1,$2,1,'Savings',$3,$3,'Active',CURRENT_DATE) RETURNING account_id", connection, setup);
        cmd.Parameters.AddWithValue($"DEMO-{Guid.NewGuid():N}"[..30]); cmd.Parameters.AddWithValue(customerId); cmd.Parameters.AddWithValue(i == 0 ? 100m : 0m);
        ids.Add((int)(await cmd.ExecuteScalarAsync())!);
    }
    await setup.CommitAsync();
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<object> Attempt(int destination)
    {
        await start.Task;
        try { var result = await service.TransferAsync(new(ids[0], destination, 80m), "Demo concurrente"); return new { destination, success = true, message = result.Status }; }
        catch (Exception ex) when (ex is BusinessException or DependencyException) { return new { destination, success = false, message = ex.Message }; }
    }
    var first = Attempt(ids[1]); var second = Attempt(ids[2]); start.SetResult();
    var attempts = await Task.WhenAll(first, second);
    var repository = new BankRepository(source, app.Services.GetRequiredService<ILogger<BankRepository>>());
    return Results.Ok(new { initialBalance = 100, amountEach = 80, accounts = ids,
        attempts, balances = await repository.QueryAsync("SELECT account_id,balance FROM account WHERE customer_id=$1 ORDER BY account_id", customerId),
        explanation = "FOR UPDATE serializa el acceso al saldo: debe haber un COMMIT y un rechazo por saldo insuficiente; saldo origen final 20. Si el proveedor esta fallando, recuperelo antes de repetir." });
});
app.Run();

static void ValidateCustomer(CustomerInput input)
{
    if (string.IsNullOrWhiteSpace(input.FirstName) || input.FirstName.Trim().Length > 50 ||
        string.IsNullOrWhiteSpace(input.LastName) || input.LastName.Trim().Length > 50 ||
        string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 120 || !System.Net.Mail.MailAddress.TryCreate(input.Email, out _) ||
        string.IsNullOrWhiteSpace(input.City) || input.City.Trim().Length > 60)
        throw new BusinessException("Nombre y apellido: 1-50 caracteres; correo valido: hasta 120; ciudad: 1-60");
}
