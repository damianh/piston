using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Measurement.Persistence;

public sealed class DatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString => _container!.GetConnectionString();

    public async Task InitializeAsync()
    {
        var image = Environment.GetEnvironmentVariable("MEASUREMENT_POSTGRES_IMAGE")
            ?? throw new InvalidOperationException("An approved digest-pinned image is required.");
        if (!image.StartsWith("postgres@", StringComparison.Ordinal) || !image.Contains("sha256:"))
            throw new InvalidOperationException("Use postgres@sha256:<digest>, not a mutable tag.");
        var trial = Environment.GetEnvironmentVariable("MEASUREMENT_TRIAL_ID")
            ?? throw new InvalidOperationException("MEASUREMENT_TRIAL_ID is required.");
        _container = new PostgreSqlBuilder(image)
            .WithDatabase($"orders_{Guid.NewGuid():N}")
            .WithLabel("piston.measurement.trial", trial)
            .WithCreateParameterModifier(parameters =>
            {
                var config = parameters.HostConfig
                    ?? throw new InvalidOperationException("Container host configuration is required.");
                config.Memory = 512L * 1024 * 1024;
                config.NanoCPUs = 1_000_000_000;
            })
            .Build();
        await _container.StartAsync();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "create table orders (id uuid primary key, quantity integer not null)", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

public sealed class PersistenceTests(DatabaseFixture database) : IClassFixture<DatabaseFixture>
{
    private async Task<NpgsqlConnection> Open()
    {
        var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    [Fact]
    public async Task IdempotentWrites()
    {
        await using var connection = await Open();
        var id = Guid.NewGuid();
        await OrderStore.Write(connection, id);
        await OrderStore.Write(connection, id);
        await using var command = new NpgsqlCommand("select count(*) from orders where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UniqueOrderIds()
    {
        await using var connection = await Open();
        var id = Guid.NewGuid();
        await OrderStore.Write(connection, id);
        await using var command = new NpgsqlCommand("insert into orders values (@id, 1)", connection);
        command.Parameters.AddWithValue("id", id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23505", error.SqlState);
    }

    [Fact]
    public async Task TransactionRollback()
    {
        await using var connection = await Open();
        var id = Guid.NewGuid();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await OrderStore.Write(connection, id);
            await transaction.RollbackAsync();
        }
        await using var command = new NpgsqlCommand("select count(*) from orders where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ConcurrentUpdates()
    {
        var id = Guid.NewGuid();
        await using (var connection = await Open())
            await OrderStore.Write(connection, id);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = await Open();
            await using var command = new NpgsqlCommand(
                "update orders set quantity = quantity + 1 where id = @id", connection);
            command.Parameters.AddWithValue("id", id);
            await command.ExecuteNonQueryAsync();
        }));
        await using var check = await Open();
        await using var select = new NpgsqlCommand("select quantity from orders where id = @id", check);
        select.Parameters.AddWithValue("id", id);
        Assert.Equal(9, await select.ExecuteScalarAsync());
    }

    [Fact]
    public void ExistingFailure() => Assert.False(OrderStore.KnownFailure);
}
