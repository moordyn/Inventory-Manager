using System.Security.Cryptography;
using System.Text;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var connectionString = builder.Configuration.GetConnectionString("Inventory")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Inventory.");
var secret = Environment.GetEnvironmentVariable("INVENTORY_PIN")
    ?? throw new InvalidOperationException("Missing INVENTORY_PIN.");
if (secret.Length < 12 || secret.StartsWith("replace-", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Set a unique INVENTORY_PIN of at least 12 characters.");
var tokenKey = RandomNumberGenerator.GetBytes(32);
var sessions = new Dictionary<string, DateTimeOffset>();
var gate = new object();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/api/auth/login", (Login request) =>
{
    // Replace with persisted, salted password hashing and rate limiting before internet exposure.
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(request.Pin ?? ""));
    if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        return Results.Unauthorized();
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    lock (gate) sessions[token] = DateTimeOffset.UtcNow.AddHours(8);
    return Results.Ok(new { token, expiresInSeconds = 28800 });
});
app.MapGet("/api/items", async (HttpContext context) =>
{
    if (!Authorized(context)) return Results.Unauthorized();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT id, sku, name, quantity FROM items ORDER BY name LIMIT 200", connection);
    await using var reader = await cmd.ExecuteReaderAsync();
    var items = new List<object>();
    while (await reader.ReadAsync())
        items.Add(new { id = reader.GetGuid(0), sku = reader.GetString(1), name = reader.GetString(2), quantity = reader.GetInt32(3) });
    return Results.Ok(items);
});
app.MapPost("/api/items", async (HttpContext context, NewItem request) =>
{
    if (!Authorized(context)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.Sku) || string.IsNullOrWhiteSpace(request.Name) || request.Quantity < 0)
        return Results.BadRequest("SKU and name required; quantity must be nonnegative.");
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var tx = await connection.BeginTransactionAsync();
    var id = Guid.NewGuid();
    await using (var cmd = new NpgsqlCommand("INSERT INTO items (id,sku,name,quantity) VALUES (@id,@sku,@name,@qty)", connection, tx))
    {
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("sku", request.Sku.Trim());
        cmd.Parameters.AddWithValue("name", request.Name.Trim());
        cmd.Parameters.AddWithValue("qty", request.Quantity);
        await cmd.ExecuteNonQueryAsync();
    }
    if (request.Quantity > 0)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO movements (item_id,delta,before_qty,after_qty,note) VALUES (@id,@delta,0,@qty,'Initial stock')", connection, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("delta", request.Quantity);
        cmd.Parameters.AddWithValue("qty", request.Quantity);
        await cmd.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
    return Results.Created($"/api/items/{id}", new { id });
});
app.MapPost("/api/items/{id:guid}/movements", async (HttpContext context, Guid id, MovementRequest request) =>
{
    if (!Authorized(context)) return Results.Unauthorized();
    if (request.Delta == 0) return Results.BadRequest("Delta must not be zero.");
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var tx = await connection.BeginTransactionAsync();
    await using var select = new NpgsqlCommand("SELECT quantity FROM items WHERE id=@id FOR UPDATE", connection, tx);
    select.Parameters.AddWithValue("id", id);
    var current = await select.ExecuteScalarAsync();
    if (current is null) return Results.NotFound();
    var before = (int)current;
    var after = (long)before + request.Delta;
    if (after < 0 || after > int.MaxValue) return Results.BadRequest("Insufficient stock or quantity overflow.");
    await using (var update = new NpgsqlCommand("UPDATE items SET quantity=@qty WHERE id=@id", connection, tx))
    {
        update.Parameters.AddWithValue("qty", (int)after);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync();
    }
    await using (var insert = new NpgsqlCommand("INSERT INTO movements (item_id,delta,before_qty,after_qty,note) VALUES (@id,@delta,@before,@after,@note)", connection, tx))
    {
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("delta", request.Delta);
        insert.Parameters.AddWithValue("before", before);
        insert.Parameters.AddWithValue("after", (int)after);
        insert.Parameters.AddWithValue("note", (object?)request.Note ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
    return Results.Ok(new { id, before, after });
});
app.MapGet("/api/movements", async (HttpContext context) =>
{
    if (!Authorized(context)) return Results.Unauthorized();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT id,item_id,delta,before_qty,after_qty,created_at,note FROM movements ORDER BY created_at DESC LIMIT 200", connection);
    await using var reader = await cmd.ExecuteReaderAsync();
    var rows = new List<object>();
    while (await reader.ReadAsync())
        rows.Add(new { id = reader.GetInt64(0), itemId = reader.GetGuid(1), delta = reader.GetInt32(2), before = reader.GetInt32(3), after = reader.GetInt32(4), timestamp = reader.GetDateTime(5), note = reader.IsDBNull(6) ? null : reader.GetString(6) });
    return Results.Ok(rows);
});
await InitializeAsync();
app.Run();

bool Authorized(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var token = header[7..].Trim();
    lock (gate) return sessions.TryGetValue(token, out var expires) && expires > DateTimeOffset.UtcNow;
}
async Task InitializeAsync()
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var cmd = new NpgsqlCommand("""
        CREATE TABLE IF NOT EXISTS items (
            id uuid PRIMARY KEY,
            sku text NOT NULL UNIQUE,
            name text NOT NULL,
            quantity integer NOT NULL DEFAULT 0 CHECK (quantity >= 0)
        );
        CREATE TABLE IF NOT EXISTS movements (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            item_id uuid NOT NULL REFERENCES items(id),
            delta integer NOT NULL CHECK (delta <> 0),
            before_qty integer NOT NULL,
            after_qty integer NOT NULL,
            note text,
            created_at timestamptz NOT NULL DEFAULT now()
        );
        """, connection);
    await cmd.ExecuteNonQueryAsync();
}
record Login(string? Pin);
record NewItem(string Sku, string Name, int Quantity);
record MovementRequest(int Delta, string? Note);
