using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SmartShop.Infrastructure.Jobs;

/// <summary>A periodic sweep (daily stock reset, cart clean-up ...). Runs in the worker under a cluster-wide lock.</summary>
public interface IRecurringJob
{
    string Name { get; }

    TimeSpan Interval { get; }

    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// Runs every registered <see cref="IRecurringJob"/> on its interval. A PostgreSQL advisory lock guarantees
/// that only one worker instance executes a given job at a time.
/// </summary>
public sealed class RecurringJobRunner(
    IServiceScopeFactory scopes,
    NpgsqlDataSource dataSource,
    TimeProvider clock,
    ILogger<RecurringJobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<(string Name, TimeSpan Interval)> jobs;
        using (var scope = scopes.CreateScope())
            jobs = scope.ServiceProvider.GetServices<IRecurringJob>().Select(j => (j.Name, j.Interval)).ToList();

        var nextRun = jobs.ToDictionary(j => j.Name, _ => clock.GetUtcNow());
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (name, interval) in jobs)
            {
                if (clock.GetUtcNow() < nextRun[name]) continue;
                nextRun[name] = clock.GetUtcNow() + interval;
                await RunOnceAsync(name, stoppingToken);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(string name, CancellationToken ct)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var lockId = StableHash(name);
            await using (var cmd = new NpgsqlCommand("select pg_try_advisory_lock(@id)", connection))
            {
                cmd.Parameters.AddWithValue("id", lockId);
                if (await cmd.ExecuteScalarAsync(ct) is not true) return;
            }

            try
            {
                using var scope = scopes.CreateScope();
                var job = scope.ServiceProvider.GetServices<IRecurringJob>().Single(j => j.Name == name);
                await job.RunAsync(ct);
            }
            finally
            {
                await using var unlock = new NpgsqlCommand("select pg_advisory_unlock(@id)", connection);
                unlock.Parameters.AddWithValue("id", lockId);
                await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Recurring job {Job} failed", name);
        }
    }

    private static long StableHash(string value)
    {
        long hash = 1125899906842597L;
        foreach (var c in value) hash = (31 * hash) + c;
        return hash;
    }
}
