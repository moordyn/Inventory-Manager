using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
var cs = builder.Configuration.GetConnectionString("Inventory") ?? throw new Exception("Missing database connection");
var setupToken = Environment.GetEnvironmentVariable("SETUP_TOKEN") ?? throw new Exception("Missing setup token");
if (setupToken.Length < 32 || setupToken.StartsWith("replace-")) throw new Exception("Invalid setup token");
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
static bool PinValid(string? p) => p is not null && Regex.IsMatch(p, @"^[0-9]{6}$");
static string PinHash(string pin, byte[] salt) => Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(pin, salt, 600_000, HashAlgorithmName.SHA256, 32));
async Task<NpgsqlConnection> Connect() { var c = new NpgsqlConnection(cs); await c.OpenAsync(); return c; }
async Task<bool> Authed(HttpContext ctx)
{
    var bearer = ctx.Request.Headers.Authorization.ToString();
    if (!bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var token = bearer[7..].Trim();
    if (token.Length != 64) return false;
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM sessions WHERE token_hash=@h AND expires_at>now())", c);
    cmd.Parameters.AddWithValue("h", Hash(token));
    return (bool)(await cmd.ExecuteScalarAsync() ?? false);
}
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/auth/status", async () =>
{
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM settings WHERE key='pin')", c);
    return Results.Ok(new { initialized = (bool)(await cmd.ExecuteScalarAsync() ?? false) });
});
app.MapPost("/api/auth/setup", async (Setup request) =>
{
    if (!PinValid(request.Pin) || !PinValid(request.ConfirmPin) || request.Pin != request.ConfirmPin) return Results.BadRequest("PIN must be six matching digits.");
    if (!Equal(Hash(request.SetupToken ?? ""), Hash(setupToken))) return Results.Unauthorized();
    var salt = RandomNumberGenerator.GetBytes(32);
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("INSERT INTO settings (key,value) VALUES ('pin',@v) ON CONFLICT DO NOTHING", c);
    cmd.Parameters.AddWithValue("v", Convert.ToHexString(salt) + ":" + PinHash(request.Pin!, salt));
    return await cmd.ExecuteNonQueryAsync() == 1 ? Results.Ok(new { created = true }) : Results.Conflict("PIN already configured.");
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/login", async (Login request) =>
{
    if (!PinValid(request.Pin)) return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT value FROM settings WHERE key='pin'", c);
    var stored = (string?)await cmd.ExecuteScalarAsync();
    if (stored is null) return Results.Conflict("Initial setup required.");
    var parts = stored.Split(':');
    if (parts.Length != 2 || !Equal(PinHash(request.Pin!, Convert.FromHexString(parts[0])), parts[1])) return Results.Unauthorized();
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    await using var insert = new NpgsqlCommand("INSERT INTO sessions(token_hash,expires_at) VALUES(@h,now()+interval '8 hours')", c);
    insert.Parameters.AddWithValue("h", Hash(token));
    await insert.ExecuteNonQueryAsync();
    return Results.Ok(new { token });
}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("DELETE FROM sessions WHERE token_hash=@h", c);
    cmd.Parameters.AddWithValue("h", Hash(ctx.Request.Headers.Authorization.ToString()[7..].Trim()));
    await cmd.ExecuteNonQueryAsync();
    return Results.Ok();
});
app.MapGet("/api/items", async (HttpContext ctx) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT id,sku,name,quantity,COALESCE(category,''),COALESCE(barcode,''),min_stock,unit_cost,COALESCE(description,'') FROM items ORDER BY name LIMIT 1000", c);
    await using var r = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await r.ReadAsync()) list.Add(new { id=r.GetGuid(0),sku=r.GetString(1),name=r.GetString(2),quantity=r.GetInt32(3),category=r.GetString(4),barcode=r.GetString(5),minStock=r.GetInt32(6),unitCost=r.IsDBNull(7)?(decimal?)null:r.GetDecimal(7),description=r.GetString(8) });
    return Results.Ok(list);
});
app.MapPost("/api/items", async (HttpContext ctx, NewItem req) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.Name) || req.Quantity < 0 || req.MinStock < 0 || req.UnitCost < 0) return Results.BadRequest("Invalid item");
    await using var c = await Connect();
    await using var tx = await c.BeginTransactionAsync();
    var id = Guid.NewGuid();
    await using (var cmd = new NpgsqlCommand("INSERT INTO items(id,sku,name,quantity,barcode,category,description,min_stock,unit_cost) VALUES(@id,@sku,@name,@qty,NULLIF(@barcode,''),@category,@description,@min,@cost)",c,tx))
    {
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("sku",req.Sku.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());cmd.Parameters.AddWithValue("qty",req.Quantity);cmd.Parameters.AddWithValue("barcode",(req.Barcode??"").Trim());cmd.Parameters.AddWithValue("category",req.Category??"");cmd.Parameters.AddWithValue("description",req.Description??"");cmd.Parameters.AddWithValue("min",req.MinStock);cmd.Parameters.AddWithValue("cost",(object?)req.UnitCost??DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    if (req.Quantity > 0)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO movements(item_id,delta,before_qty,after_qty,note) VALUES(@id,@delta,0,@delta,'Initial stock')",c,tx);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("delta",req.Quantity);await cmd.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
    return Results.Created($"/api/items/{id}",new{id});
});
app.MapPost("/api/items/{id:guid}/movements", async (HttpContext ctx, Guid id, MovementRequest req) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    if (req.Delta==0 || req.IdempotencyKey==Guid.Empty) return Results.BadRequest("Nonzero delta and idempotency key required");
    await using var c = await Connect();
    await using var tx = await c.BeginTransactionAsync();
    await using (var duplicate = new NpgsqlCommand("SELECT after_qty FROM movements WHERE idempotency_key=@key",c,tx))
    {
        duplicate.Parameters.AddWithValue("key",req.IdempotencyKey);
        var found = await duplicate.ExecuteScalarAsync();
        if (found is not null) return Results.Ok(new { after=(int)found,duplicate=true });
    }
    await using var select = new NpgsqlCommand("SELECT quantity FROM items WHERE id=@id FOR UPDATE",c,tx);
    select.Parameters.AddWithValue("id",id);
    var current=await select.ExecuteScalarAsync();
    if (current is null) return Results.NotFound();
    var before=(int)current;
    var after=(long)before+req.Delta;
    if (after<0 || after>int.MaxValue) return Results.BadRequest("Insufficient stock or quantity overflow");
    await using (var update = new NpgsqlCommand("UPDATE items SET quantity=@qty WHERE id=@id",c,tx))
    {
        update.Parameters.AddWithValue("qty",(int)after);update.Parameters.AddWithValue("id",id);await update.ExecuteNonQueryAsync();
    }
    await using (var insert = new NpgsqlCommand("INSERT INTO movements(item_id,delta,before_qty,after_qty,note,idempotency_key) VALUES(@id,@delta,@before,@after,@note,@key) ON CONFLICT (idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING",c,tx))
    {
        insert.Parameters.AddWithValue("id",id);insert.Parameters.AddWithValue("delta",req.Delta);insert.Parameters.AddWithValue("before",before);insert.Parameters.AddWithValue("after",(int)after);
        insert.Parameters.AddWithValue("note",(object?)req.Note??DBNull.Value);insert.Parameters.AddWithValue("key",req.IdempotencyKey);
        if (await insert.ExecuteNonQueryAsync()==0) { await tx.RollbackAsync(); return Results.Conflict("Retry request after concurrent transaction"); }
    }
    await tx.CommitAsync();
    return Results.Ok(new { before,after });
});
app.MapGet("/api/movements", async (HttpContext ctx) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT m.id,i.sku,i.name,m.delta,m.before_qty,m.after_qty,m.created_at,m.note FROM movements m JOIN items i ON i.id=m.item_id ORDER BY m.id DESC LIMIT 200",c);
    await using var r=await cmd.ExecuteReaderAsync();
    var list=new List<object>();
    while(await r.ReadAsync()) list.Add(new{id=r.GetInt64(0),sku=r.GetString(1),name=r.GetString(2),delta=r.GetInt32(3),before=r.GetInt32(4),after=r.GetInt32(5),time=r.GetDateTime(6),note=r.IsDBNull(7)?null:r.GetString(7)});
    return Results.Ok(list);
});

app.MapGet("/api/summary", async (HttpContext ctx) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT count(*),COALESCE(sum(quantity),0),count(*) FILTER(WHERE quantity<=min_stock),COALESCE(sum(quantity*unit_cost),0) FROM items",c);
    await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();
    return Results.Ok(new{products=r.GetInt64(0),units=r.GetInt64(1),lowStock=r.GetInt64(2),value=r.GetDecimal(3)});
});
app.MapGet("/api/items/lookup/{code}", async (HttpContext ctx, string code) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT id,sku,name,quantity FROM items WHERE sku=@code OR barcode=@code LIMIT 1",c);
    cmd.Parameters.AddWithValue("code",code);
    await using var r=await cmd.ExecuteReaderAsync();
    return await r.ReadAsync()?Results.Ok(new{id=r.GetGuid(0),sku=r.GetString(1),name=r.GetString(2),quantity=r.GetInt32(3)}):Results.NotFound();
});
app.MapPut("/api/items/{id:guid}", async (HttpContext ctx, Guid id, EditItem req) =>
{
    if (!await Authed(ctx)) return Results.Unauthorized();
    if(string.IsNullOrWhiteSpace(req.Name)||string.IsNullOrWhiteSpace(req.Sku)||req.MinStock<0||req.UnitCost<0)return Results.BadRequest("Invalid item details");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("UPDATE items SET sku=@sku,name=@name,barcode=NULLIF(@barcode,''),category=@category,description=@description,min_stock=@min,unit_cost=@cost,updated_at=now() WHERE id=@id",c);
    cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("sku",req.Sku.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());
    cmd.Parameters.AddWithValue("barcode",(req.Barcode??"").Trim());cmd.Parameters.AddWithValue("category",(req.Category??"").Trim());cmd.Parameters.AddWithValue("description",(req.Description??"").Trim());
    cmd.Parameters.AddWithValue("min",req.MinStock);cmd.Parameters.AddWithValue("cost",(object?)req.UnitCost??DBNull.Value);
    return await cmd.ExecuteNonQueryAsync()==0?Results.NotFound():Results.Ok(new{updated=true});
});

await using (var c=await Connect())
{
    await using var cmd=new NpgsqlCommand("""
    CREATE TABLE IF NOT EXISTS settings(key text PRIMARY KEY,value text NOT NULL);
    CREATE TABLE IF NOT EXISTS sessions(token_hash text PRIMARY KEY,expires_at timestamptz NOT NULL);
    CREATE TABLE IF NOT EXISTS items(id uuid PRIMARY KEY,sku text NOT NULL UNIQUE,name text NOT NULL,quantity integer NOT NULL DEFAULT 0 CHECK(quantity>=0));
    CREATE TABLE IF NOT EXISTS movements(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,item_id uuid NOT NULL REFERENCES items(id),delta integer NOT NULL CHECK(delta<>0),before_qty integer NOT NULL,after_qty integer NOT NULL,note text,created_at timestamptz NOT NULL DEFAULT now());
    ALTER TABLE items ADD COLUMN IF NOT EXISTS category text NOT NULL DEFAULT '';
    ALTER TABLE items ADD COLUMN IF NOT EXISTS description text NOT NULL DEFAULT '';
    ALTER TABLE items ADD COLUMN IF NOT EXISTS barcode text;
    ALTER TABLE items ADD COLUMN IF NOT EXISTS min_stock integer NOT NULL DEFAULT 0;
    ALTER TABLE items ADD COLUMN IF NOT EXISTS unit_cost numeric(12,2);
    ALTER TABLE items ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
    CREATE UNIQUE INDEX IF NOT EXISTS items_barcode_idx ON items(barcode) WHERE barcode IS NOT NULL;
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS idempotency_key uuid;
    CREATE UNIQUE INDEX IF NOT EXISTS movements_idempotency_key_idx ON movements(idempotency_key) WHERE idempotency_key IS NOT NULL;
    """,c);
    await cmd.ExecuteNonQueryAsync();
}
app.Run();
record Setup(string? SetupToken,string? Pin,string? ConfirmPin);
record Login(string? Pin);
record NewItem(string Sku,string Name,int Quantity,string? Barcode="",string? Category="",string? Description="",int MinStock=0,decimal? UnitCost=null);
record MovementRequest(int Delta,string? Note,Guid IdempotencyKey);
record EditItem(string Sku,string Name,string? Barcode,string? Category,string? Description,int MinStock,decimal? UnitCost);
