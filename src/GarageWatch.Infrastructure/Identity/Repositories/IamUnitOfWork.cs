using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity.Repositories;

public sealed class IamTransactionGate
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class IamUnitOfWork(IamDbContext database, IamTransactionGate transactionGate) : IIamUnitOfWork
{
    private readonly AsyncLocal<bool> _isExecuting = new();

    public async Task<TResult> ExecuteAtomicallyAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_isExecuting.Value) throw new InvalidOperationException("Nested IAM UnitOfWork operations are not supported.");
        await transactionGate.Semaphore.WaitAsync(cancellationToken);
        _isExecuting.Value = true;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var result = await operation(cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _isExecuting.Value = false;
            transactionGate.Semaphore.Release();
        }
    }

    public async Task ExecuteAtomicallyAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAtomicallyAsync(async token =>
        {
            await operation(token);
            return true;
        }, cancellationToken);
    }
}
