using BankingApp.Domain;
using Npgsql;

namespace BankingApp.Data;

// ================ Aqui se aplico SOLID S: el repositorio solo se ocupa de persistencia SQL ================
public sealed class BankRepository(NpgsqlDataSource source, ILogger<BankRepository> logger)
    : ICustomerRepository, IBankQueries, ITransferRepository
{
    public async Task<object> QueryAsync(string sql, params object[] parameters)
    {
        await using var cmd = source.CreateCommand(sql);
        foreach (var parameter in parameters) cmd.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    public Task<object> ListAsync() => QueryAsync("SELECT customer_id,first_name,last_name,email,city,is_deleted FROM customer WHERE NOT is_deleted ORDER BY customer_id");
    public Task<object> AccountsAsync() => QueryAsync("SELECT a.account_id,a.account_number,a.customer_id,concat(c.first_name,' ',c.last_name) customer,a.balance,a.status FROM account a JOIN customer c USING(customer_id) ORDER BY a.account_id");
    public Task<object> TransactionsAsync() => QueryAsync("SELECT t.transaction_id,t.account_id,t.destination_account_id,t.transaction_type,t.amount,t.transaction_time,t.description FROM bank_transaction t ORDER BY transaction_id DESC LIMIT 100");
    public Task<object> LoansAsync() => QueryAsync("SELECT l.loan_id,l.customer_id,l.loan_amount,l.remaining_balance,l.status,COUNT(p.payment_id) payments FROM loan l LEFT JOIN loan_payment p USING(loan_id) GROUP BY l.loan_id ORDER BY l.loan_id");
    public Task<object> DashboardAsync() => QueryAsync("SELECT (SELECT count(*) FROM customer WHERE NOT is_deleted) customers,(SELECT count(*) FROM account) accounts,(SELECT sum(balance) FROM account) total_balance,(SELECT count(*) FROM bank_transaction) transactions");
    public async Task<int> CreateAsync(CustomerInput input)
    {
        await using var cmd = source.CreateCommand("INSERT INTO customer(first_name,last_name,email,city) VALUES($1,$2,$3,$4) RETURNING customer_id");
        AddCustomerParameters(cmd, input);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }
    public async Task UpdateAsync(int id, CustomerInput input)
    {
        await using var cmd = source.CreateCommand("UPDATE customer SET first_name=$1,last_name=$2,email=$3,city=$4 WHERE customer_id=$5 AND NOT is_deleted");
        AddCustomerParameters(cmd, input); cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync() == 0) throw new BusinessException("Cliente no encontrado");
    }
    public async Task DeleteAsync(int id)
    {
        // Borrado logico preserva las relaciones y el historial bancario.
        await using var cmd = source.CreateCommand("UPDATE customer SET is_deleted=TRUE WHERE customer_id=$1 AND NOT is_deleted");
        cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync() == 0) throw new BusinessException("Cliente no encontrado");
    }
    private static void AddCustomerParameters(NpgsqlCommand cmd, CustomerInput input)
    {
        cmd.Parameters.AddWithValue(input.FirstName.Trim()); cmd.Parameters.AddWithValue(input.LastName.Trim());
        cmd.Parameters.AddWithValue(input.Email.Trim()); cmd.Parameters.AddWithValue(input.City.Trim());
    }

    public async Task<long> ExecuteAsync(TransferRequest request)
    {
        await using var connection = await source.OpenConnectionAsync();
        // ================ Aqui se aplico ACID: transaccion real en una sola conexion ================
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        try
        {
            // ================ Aqui se aplico Aislamiento: bloqueo FOR UPDATE en orden de ID ================
            // Dos solicitudes sobre la misma cuenta esperan y comprueban el saldo actualizado.
            await using var select = new NpgsqlCommand("SELECT account_id,balance,status FROM account WHERE account_id IN ($1,$2) ORDER BY account_id FOR UPDATE", connection, transaction);
            select.Parameters.AddWithValue(request.FromAccountId); select.Parameters.AddWithValue(request.ToAccountId);
            var accounts = new Dictionary<int, (decimal Balance, string Status)>();
            await using (var reader = await select.ExecuteReaderAsync())
                while (await reader.ReadAsync()) accounts[reader.GetInt32(0)] = (reader.GetDecimal(1), reader.GetString(2));
            if (accounts.Count != 2 || accounts.Values.Any(a => a.Status != "Active")) throw new BusinessException("Ambas cuentas deben existir y estar activas");
            if (accounts[request.FromAccountId].Balance < request.Amount) throw new BusinessException("Saldo insuficiente; operacion revertida");
            await using var debit = new NpgsqlCommand("UPDATE account SET balance=balance-$1 WHERE account_id=$2", connection, transaction);
            debit.Parameters.AddWithValue(request.Amount); debit.Parameters.AddWithValue(request.FromAccountId);
            await debit.ExecuteNonQueryAsync();
            // ================ Aqui se demostro Atomicidad: error despues del debito provoca ROLLBACK ================
            if (request.SimulateError) throw new BusinessException("ERROR SIMULADO despues del debito: ROLLBACK ejecutado");
            await using var credit = new NpgsqlCommand("UPDATE account SET balance=balance+$1 WHERE account_id=$2", connection, transaction);
            credit.Parameters.AddWithValue(request.Amount); credit.Parameters.AddWithValue(request.ToAccountId);
            await credit.ExecuteNonQueryAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO bank_transaction(account_id,transaction_type,amount,destination_account_id,description) VALUES($1,'Transfer',$2,$3,'Transferencia desde Banking U2') RETURNING transaction_id", connection, transaction);
            insert.Parameters.AddWithValue(request.FromAccountId); insert.Parameters.AddWithValue(request.Amount); insert.Parameters.AddWithValue(request.ToAccountId);
            var id = (long)(await insert.ExecuteScalarAsync())!;
            // ================ Aqui se aplico Durabilidad: COMMIT y volumen persistente de PostgreSQL ================
            await transaction.CommitAsync();
            logger.LogInformation("COMMIT transferencia {Id}; origen {From}, destino {To}, monto {Amount}", id, request.FromAccountId, request.ToAccountId, request.Amount);
            return id;
        }
        catch
        {
            await transaction.RollbackAsync();
            logger.LogWarning("ROLLBACK: ningun saldo ni movimiento parcial se conserva");
            throw;
        }
    }
}
