namespace BankingApp.Domain;

public record CustomerInput(string FirstName, string LastName, string Email, string City);
public record TransferRequest(int FromAccountId, int ToAccountId, decimal Amount, bool SimulateError = false);
public record TransferResult(long TransactionId, string Status, IReadOnlyList<string> Observers);
public record TransferEvent(long TransactionId, int FromAccountId, int ToAccountId, decimal Amount);
public sealed class BusinessException(string message) : Exception(message);
public sealed class DependencyException(string message) : Exception(message);

// ================ Aqui se aplico SOLID I: contratos pequenos para responsabilidades distintas ================
public interface ICustomerRepository
{
    Task<object> ListAsync();
    Task<int> CreateAsync(CustomerInput input);
    Task UpdateAsync(int id, CustomerInput input);
    Task DeleteAsync(int id);
}
public interface IBankQueries
{
    Task<object> AccountsAsync();
    Task<object> TransactionsAsync();
    Task<object> LoansAsync();
    Task<object> DashboardAsync();
}
public interface ITransferRepository { Task<long> ExecuteAsync(TransferRequest request); }
public interface ITransferService { Task<TransferResult> TransferAsync(TransferRequest request, string actor); }
public interface ITransferAuthorizer { Task AuthorizeAsync(TransferRequest request); }
public interface ITransferObserver { Task<string> OnConfirmedAsync(TransferEvent transfer); }
