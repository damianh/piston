using Npgsql;

namespace Measurement.Persistence;

public static class OrderStore
{
    public const bool KnownFailure = false;
    public const string ConflictClause = "on conflict (id) do nothing";

    public static async Task Write(NpgsqlConnection connection, Guid id)
    {
        await using var command = new NpgsqlCommand(
            $"insert into orders (id, quantity) values (@id, 1) {ConflictClause}", connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}
