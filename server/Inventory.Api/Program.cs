using System.Net;
using System.Net.Mail;
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
    var employeeId=string.IsNullOrWhiteSpace(request.EmployeeId)?"OWNER":request.EmployeeId.Trim();
    await using(var check=new NpgsqlCommand("SELECT count(*) FROM employees WHERE id=@employee AND active AND global_admin",c)){
        check.Parameters.AddWithValue("employee",employeeId);
        if((long)(await check.ExecuteScalarAsync()??0L)!=1)return Results.Unauthorized();
    }
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    await using var insert = new NpgsqlCommand("INSERT INTO sessions(token_hash,expires_at,store_id,employee_id,is_admin) SELECT @h,now()+interval '8 hours',e.primary_store_id,e.id,true FROM employees e WHERE e.id=@employee AND e.global_admin AND e.active LIMIT 1", c);
    insert.Parameters.AddWithValue("h", Hash(token));insert.Parameters.AddWithValue("employee",employeeId);
    if(await insert.ExecuteNonQueryAsync()!=1)return Results.Unauthorized();
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

async Task<(string store,string employee,bool admin,bool globalAdmin)?> Identity(HttpContext ctx)
{
    var auth=ctx.Request.Headers.Authorization.ToString();
    if(!auth.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase))return null;
    var token=auth[7..].Trim();
    if(token.Length!=64)return null;
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT sess.store_id,sess.employee_id,sess.is_admin FROM sessions sess WHERE sess.token_hash=@hash AND sess.expires_at>now() AND ((sess.is_admin AND EXISTS(SELECT 1 FROM employees e WHERE e.id=sess.employee_id AND e.active AND e.global_admin)) OR (EXISTS(SELECT 1 FROM employees e WHERE e.id=sess.employee_id AND e.active) AND EXISTS(SELECT 1 FROM employee_store_access a JOIN stores st ON st.id=a.store_id WHERE a.employee_id=sess.employee_id AND a.store_id=sess.store_id AND st.active)))",c);
    cmd.Parameters.AddWithValue("hash",Hash(token));
    await using var reader=await cmd.ExecuteReaderAsync();
    if(!await reader.ReadAsync())return null;
    var store=reader.GetString(0);var employee=reader.GetString(1);var pinSession=reader.GetBoolean(2);
    await reader.CloseAsync();
    await using var roleCmd=new NpgsqlCommand("SELECT COALESCE((SELECT global_admin FROM employees WHERE id=@employee LIMIT 1),false),COALESCE((SELECT role='store_admin' FROM employee_store_access WHERE employee_id=@employee AND store_id=@store),false)",c);
    roleCmd.Parameters.AddWithValue("employee",employee);roleCmd.Parameters.AddWithValue("store",store);
    await using var roles=await roleCmd.ExecuteReaderAsync();await roles.ReadAsync();
    var global=pinSession&&roles.GetBoolean(0);
    return(store,employee,global||roles.GetBoolean(1),global);
}
app.MapGet("/api/auth/me",async (HttpContext ctx)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT name FROM employees WHERE id=@employee ORDER BY CASE WHEN primary_store_id=@store THEN 0 ELSE 1 END LIMIT 1",c);
    cmd.Parameters.AddWithValue("employee",who.Value.employee);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    var employeeName=(string?)await cmd.ExecuteScalarAsync()??who.Value.employee;
    return Results.Ok(new{storeId=who.Value.store,employeeId=who.Value.employee,employeeName,isAdmin=who.Value.admin,isGlobalAdmin=who.Value.globalAdmin});
});
app.MapPost("/api/auth/employee-login",async (EmployeeLogin req)=>{
    if(string.IsNullOrWhiteSpace(req.EmployeeId))return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT a.store_id FROM employee_store_access a JOIN employees e ON e.id=a.employee_id JOIN stores s ON s.id=a.store_id WHERE a.employee_id=@employee AND e.active AND s.active ORDER BY CASE WHEN a.store_id=e.primary_store_id THEN 0 ELSE 1 END,a.store_id LIMIT 1",c);
    cmd.Parameters.AddWithValue("employee",req.EmployeeId.Trim());
    await using(var count=new NpgsqlCommand("SELECT count(*) FROM employees WHERE id=@employee AND active",c)){
        count.Parameters.AddWithValue("employee",req.EmployeeId.Trim());
        if((long)(await count.ExecuteScalarAsync()??0L)!=1)return Results.Unauthorized();
    }
    var store=(string?)await cmd.ExecuteScalarAsync();
    if(store is null)return Results.Unauthorized();
    var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    await using var insert=new NpgsqlCommand("INSERT INTO sessions(token_hash,expires_at,store_id,employee_id,is_admin) VALUES(@hash,now()+interval '8 hours',@store,@employee,false)",c);
    insert.Parameters.AddWithValue("hash",Hash(token));insert.Parameters.AddWithValue("store",store);insert.Parameters.AddWithValue("employee",req.EmployeeId.Trim());
    await insert.ExecuteNonQueryAsync();
    return Results.Ok(new{token});
}).RequireRateLimiting("auth");
app.MapGet("/api/auth/stores",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT s.id,s.name FROM stores s JOIN employee_store_access a ON a.store_id=s.id WHERE a.employee_id=@employee AND s.active ORDER BY s.name",c);
    cmd.Parameters.AddWithValue("employee",who.Value.employee);
    await using var r=await cmd.ExecuteReaderAsync();var stores=new List<object>();
    while(await r.ReadAsync())stores.Add(new{id=r.GetString(0),name=r.GetString(1)});
    return Results.Ok(stores);
});
app.MapPost("/api/auth/select-store",async (HttpContext ctx,SelectStore req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    
    await using var c=await Connect();
    await using(var check=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employee_store_access a JOIN stores s ON s.id=a.store_id WHERE (a.employee_id=@employee AND a.store_id=@store AND s.active))",c)){
        check.Parameters.AddWithValue("employee",who.Value.employee);check.Parameters.AddWithValue("store",req.StoreId??"");
        if(!who.Value.globalAdmin && !(bool)(await check.ExecuteScalarAsync()??false))return Results.Forbid();
        if(who.Value.globalAdmin){await using var exists=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM stores WHERE id=@store AND active)",c);exists.Parameters.AddWithValue("store",req.StoreId??"");if(!(bool)(await exists.ExecuteScalarAsync()??false))return Results.Forbid();}
    }
    var auth=ctx.Request.Headers.Authorization.ToString()[7..].Trim();
    await using var cmd=new NpgsqlCommand("UPDATE sessions SET store_id=@store WHERE token_hash=@hash AND employee_id=@employee AND expires_at>now()",c);
    cmd.Parameters.AddWithValue("store",req.StoreId!);cmd.Parameters.AddWithValue("hash",Hash(auth));cmd.Parameters.AddWithValue("employee",who.Value.employee);
    return await cmd.ExecuteNonQueryAsync()==1?Results.Ok(new{storeId=req.StoreId}):Results.Unauthorized();
});
app.MapGet("/api/admin/stores",async (HttpContext ctx)=>{
    if((await Identity(ctx))?.globalAdmin!=true)return Results.Forbid();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT id,name,active FROM stores ORDER BY id",c);
    await using var r=await cmd.ExecuteReaderAsync();var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{id=r.GetString(0),name=r.GetString(1),active=r.GetBoolean(2)});
    return Results.Ok(list);
});
app.MapPost("/api/admin/stores",async (HttpContext ctx,StoreRequest req)=>{
    if((await Identity(ctx))?.admin!=true)return Results.Unauthorized();
    if(!Regex.IsMatch(req.Id??"",@"^[A-Za-z0-9_-]{2,24}$")||string.IsNullOrWhiteSpace(req.Name))return Results.BadRequest("Invalid store details");
    await using var c=await Connect();await using var cmd=new NpgsqlCommand("INSERT INTO stores(id,name) VALUES(@id,@name)",c);
    cmd.Parameters.AddWithValue("id",req.Id.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());
    await cmd.ExecuteNonQueryAsync();return Results.Ok(new{created=true});
});
app.MapPut("/api/admin/stores/{id}/name",async (HttpContext ctx,string id,StoreNameRequest req)=>{
    if((await Identity(ctx))?.admin!=true)return Results.Unauthorized();
    var name=req.Name?.Trim();
    if(string.IsNullOrWhiteSpace(name)||name.Length>100)return Results.BadRequest("Location name must be 1–100 characters.");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("UPDATE stores SET name=@name WHERE id=@id",c);
    cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("name",name);
    return await cmd.ExecuteNonQueryAsync()==1?Results.Ok(new{updated=true}):Results.NotFound();
});
app.MapPut("/api/admin/stores/{id}",async (HttpContext ctx,string id,StoreRequest req)=>{
    if((await Identity(ctx))?.admin!=true)return Results.Unauthorized();
    var newId=req.Id?.Trim();var name=req.Name?.Trim();
    if(newId is null||!Regex.IsMatch(newId,@"^[A-Za-z0-9_-]{2,24}$")||string.IsNullOrWhiteSpace(name)||name.Length>100)
        return Results.BadRequest("Store ID must be 2–24 letters, numbers, hyphens or underscores; name must be 1–100 characters.");
    
    await using var c=await Connect();await using var tx=await c.BeginTransactionAsync();
    await using(var exists=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM stores WHERE id=@id)",c,tx)){
        exists.Parameters.AddWithValue("id",id);
        if(!(bool)(await exists.ExecuteScalarAsync()??false))return Results.NotFound();
    }
    if(id==newId){
        await using var update=new NpgsqlCommand("UPDATE stores SET name=@name WHERE id=@id",c,tx);
        update.Parameters.AddWithValue("name",name);update.Parameters.AddWithValue("id",id);
        await update.ExecuteNonQueryAsync();
    }else{
        await using(var check=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM stores WHERE id=@id)",c,tx)){
            check.Parameters.AddWithValue("id",newId);
            if((bool)(await check.ExecuteScalarAsync()??false))return Results.Conflict("That Store ID is already in use.");
        }
        await using(var create=new NpgsqlCommand("INSERT INTO stores(id,name,active) SELECT @newId,@name,active FROM stores WHERE id=@oldId",c,tx)){
            create.Parameters.AddWithValue("newId",newId);create.Parameters.AddWithValue("name",name);create.Parameters.AddWithValue("oldId",id);
            await create.ExecuteNonQueryAsync();
        }
        foreach(var table in new[]{"items","movements","employees","employee_store_access","sessions","low_stock_state","alert_outbox"}){
            await using var move=new NpgsqlCommand($"UPDATE {table} SET store_id=@newId WHERE store_id=@oldId",c,tx);
            move.Parameters.AddWithValue("newId",newId);move.Parameters.AddWithValue("oldId",id);
            await move.ExecuteNonQueryAsync();
        }
        await using(var primary=new NpgsqlCommand("UPDATE employees SET primary_store_id=@newId WHERE primary_store_id=@oldId",c,tx)){
            primary.Parameters.AddWithValue("newId",newId);primary.Parameters.AddWithValue("oldId",id);await primary.ExecuteNonQueryAsync();
        }
        await using(var remove=new NpgsqlCommand("DELETE FROM stores WHERE id=@oldId",c,tx)){
            remove.Parameters.AddWithValue("oldId",id);await remove.ExecuteNonQueryAsync();
        }
    }
    await tx.CommitAsync();return Results.Ok(new{id=newId,name});
});
app.MapGet("/api/admin/employees",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.Forbid();
    await using var c=await Connect();await using var cmd=new NpgsqlCommand("SELECT primary_store_id,id,name,active,global_admin FROM employees WHERE @global OR id IN(SELECT employee_id FROM employee_store_access WHERE store_id=@store) ORDER BY primary_store_id,id",c);
    cmd.Parameters.AddWithValue("global",who.Value.globalAdmin);cmd.Parameters.AddWithValue("store",who.Value.store);await using var r=await cmd.ExecuteReaderAsync();var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{storeId=r.GetString(0),id=r.GetString(1),name=r.GetString(2),active=r.GetBoolean(3),globalAdmin=r.GetBoolean(4)});
    return Results.Ok(list);
});
app.MapPost("/api/admin/employees",async (HttpContext ctx,EmployeeRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.Forbid();if(!who.Value.globalAdmin&&who.Value.store!=req.StoreId)return Results.Forbid();
    if(!Regex.IsMatch(req.Id??"",@"^[A-Za-z0-9_-]{2,24}$")||string.IsNullOrWhiteSpace(req.StoreId)||string.IsNullOrWhiteSpace(req.Name))return Results.BadRequest("Invalid employee details");
    await using var c=await Connect();
    await using(var duplicate=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employees WHERE id=@id)",c)){duplicate.Parameters.AddWithValue("id",req.Id.Trim());if((bool)(await duplicate.ExecuteScalarAsync()??false))return Results.Conflict("Employee ID already exists. IDs must be unique across all stores.");}
    await using var cmd=new NpgsqlCommand("INSERT INTO employees(store_id,primary_store_id,id,name) VALUES(@store,@store,@id,@name)",c);
    cmd.Parameters.AddWithValue("store",req.StoreId.Trim());cmd.Parameters.AddWithValue("id",req.Id.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());
    await cmd.ExecuteNonQueryAsync();
    await using var access=new NpgsqlCommand("INSERT INTO employee_store_access(employee_id,store_id) VALUES(@employee,@store)",c);
    access.Parameters.AddWithValue("employee",req.Id.Trim());access.Parameters.AddWithValue("store",req.StoreId.Trim());await access.ExecuteNonQueryAsync();
    return Results.Ok(new{created=true});
});

app.MapPost("/api/admin/employees/{id}/stores",async (HttpContext ctx,string id,SelectStore req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.Forbid();if(!who.Value.globalAdmin&&who.Value.store!=req.StoreId)return Results.Forbid();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("INSERT INTO employee_store_access(employee_id,store_id) VALUES(@employee,@store) ON CONFLICT DO NOTHING",c);
    cmd.Parameters.AddWithValue("employee",id);cmd.Parameters.AddWithValue("store",req.StoreId??"");
    await cmd.ExecuteNonQueryAsync();return Results.Ok(new{assigned=true});
});
app.MapGet("/api/admin/employee-access",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.Forbid();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT employee_id,store_id,role FROM employee_store_access WHERE @global OR store_id=@store ORDER BY employee_id,store_id",c);
    cmd.Parameters.AddWithValue("global",who.Value.globalAdmin);cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{employeeId=r.GetString(0),storeId=r.GetString(1),role=r.GetString(2)});
    return Results.Ok(list);
});
app.MapPut("/api/admin/employees/{id}/permissions",async (HttpContext ctx,string id,EmployeePermissions req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.Forbid();
    if(id=="OWNER" && !req.GlobalAdmin)return Results.BadRequest("The recovery administrator cannot be demoted.");
    await using var c=await Connect();await using var tx=await c.BeginTransactionAsync();
    await using(var update=new NpgsqlCommand("UPDATE employees SET global_admin=@global WHERE id=@id",c,tx)){
        update.Parameters.AddWithValue("global",req.GlobalAdmin);update.Parameters.AddWithValue("id",id);
        if(await update.ExecuteNonQueryAsync()!=1)return Results.NotFound();
    }
    await using(var clear=new NpgsqlCommand("UPDATE employee_store_access SET role='employee' WHERE employee_id=@id",c,tx)){
        clear.Parameters.AddWithValue("id",id);await clear.ExecuteNonQueryAsync();
    }
    foreach(var store in (req.StoreAdminIds??Array.Empty<string>()).Distinct()){
        await using var set=new NpgsqlCommand("INSERT INTO employee_store_access(employee_id,store_id,role) VALUES(@id,@store,'store_admin') ON CONFLICT(employee_id,store_id) DO UPDATE SET role='store_admin'",c,tx);
        set.Parameters.AddWithValue("id",id);set.Parameters.AddWithValue("store",store);
        await set.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();return Results.Ok(new{updated=true});
});

static string[] ParseRecipients(string? input)=> (input??"").Split(new[]{',',';','\n','\r'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
static bool ValidEmail(string value){try{var a=new MailAddress(value);return a.Address.Equals(value,StringComparison.OrdinalIgnoreCase)&&value.Length<=254;}catch{return false;}}
async Task<bool> CanManageStore(HttpContext ctx,string store){
    var who=await Identity(ctx);
    if(who is null)return false;
    if(who.Value.globalAdmin)return true;
    if(!who.Value.admin||who.Value.store!=store)return false;
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employee_store_access WHERE employee_id=@employee AND store_id=@store AND role='store_admin')",c);
    cmd.Parameters.AddWithValue("employee",who.Value.employee);cmd.Parameters.AddWithValue("store",store);
    return (bool)(await cmd.ExecuteScalarAsync()??false);
}
app.MapGet("/api/alerts/config",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!await CanManageStore(ctx,who.Value.store))return Results.Forbid();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT enabled,recipients FROM store_alert_settings WHERE store_id=@store",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    bool enabled=false;string recipients="";
    if(await r.ReadAsync()){enabled=r.GetBoolean(0);recipients=r.GetString(1);}
    return Results.Ok(new{enabled,recipients,senderConfigured=MailConfigured()});
});
app.MapPut("/api/alerts/config",async (HttpContext ctx,AlertSettingsRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!await CanManageStore(ctx,who.Value.store))return Results.Forbid();
    var recipients=ParseRecipients(req.Recipients);
    if(recipients.Length>10||recipients.Any(x=>!ValidEmail(x)))return Results.BadRequest("Enter up to 10 valid email addresses separated by commas.");
    if(req.Enabled&&recipients.Length==0)return Results.BadRequest("Add at least one recipient before enabling alerts.");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("INSERT INTO store_alert_settings(store_id,enabled,recipients) VALUES(@store,@enabled,@recipients) ON CONFLICT(store_id) DO UPDATE SET enabled=excluded.enabled,recipients=excluded.recipients",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);cmd.Parameters.AddWithValue("enabled",req.Enabled);cmd.Parameters.AddWithValue("recipients",string.Join(",",recipients));
    await cmd.ExecuteNonQueryAsync();return Results.Ok(new{saved=true});
});
app.MapPost("/api/alerts/test",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!await CanManageStore(ctx,who.Value.store))return Results.Forbid();
    if(!MailConfigured())return Results.BadRequest("Gmail SMTP is not configured on the server.");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT s.name,COALESCE(a.recipients,'') FROM stores s LEFT JOIN store_alert_settings a ON a.store_id=s.id WHERE s.id=@store",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    if(!await r.ReadAsync())return Results.NotFound();
    var name=r.GetString(0);var recipients=ParseRecipients(r.GetString(1));
    if(recipients.Length==0)return Results.BadRequest("Save at least one recipient first.");
    try{await SendMail(recipients,"Inventory Manager test — "+name,"Test successful. Low-stock notifications for "+name+" will be sent to this address when enabled.");return Results.Ok(new{sent=true});}
    catch(Exception ex){app.Logger.LogError(ex,"SMTP test failed for {Store}",who.Value.store);return Results.Problem("Email could not be sent. Check the server's SMTP settings and logs.",statusCode:502);}
});
bool MailConfigured()=>!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMTP_USER"))&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMTP_APP_PASSWORD"));
async Task SendMail(string[] recipients,string subject,string body){
    var user=Environment.GetEnvironmentVariable("SMTP_USER")??"";
    var password=Environment.GetEnvironmentVariable("SMTP_APP_PASSWORD")??"";
    if(!MailConfigured())throw new InvalidOperationException("SMTP credentials missing");
    using var message=new MailMessage{From=new MailAddress(user),Subject=subject,Body=body,IsBodyHtml=false};
    foreach(var recipient in recipients)message.To.Add(recipient);
    using var smtp=new SmtpClient("smtp.gmail.com",587){EnableSsl=true,UseDefaultCredentials=false,Credentials=new NetworkCredential(user,password),Timeout=15000};
    await smtp.SendMailAsync(message);
}
async Task QueueLowStock(NpgsqlConnection c,NpgsqlTransaction tx,Guid itemId,string store){
    await using var cmd=new NpgsqlCommand("""
    INSERT INTO low_stock_state(item_id,store_id,is_low)
    SELECT i.id,i.store_id,(i.quantity<=i.min_stock) FROM items i WHERE i.id=@item AND i.store_id=@store
    ON CONFLICT(item_id) DO UPDATE SET cycle=CASE WHEN low_stock_state.is_low=false AND excluded.is_low=true THEN low_stock_state.cycle+1 ELSE low_stock_state.cycle END,is_low=excluded.is_low
    RETURNING is_low
    """,c,tx);
    cmd.Parameters.AddWithValue("item",itemId);cmd.Parameters.AddWithValue("store",store);
    var low=(bool)(await cmd.ExecuteScalarAsync()??false);
    if(!low)return;
    await using var queue=new NpgsqlCommand("""
    INSERT INTO alert_outbox(store_id,item_id,alert_cycle,subject,body)
    SELECT i.store_id,i.id,(SELECT cycle FROM low_stock_state WHERE item_id=i.id),'Low stock: '||i.name,
           'Store: '||s.name||E'\\nProduct: '||i.name||E'\\nSKU: '||i.sku||E'\\nCurrent stock: '||i.quantity||E'\\nMinimum stock: '||i.min_stock
    FROM items i JOIN stores s ON s.id=i.store_id JOIN store_alert_settings a ON a.store_id=i.store_id
    WHERE i.id=@item AND i.store_id=@store AND a.enabled AND a.recipients<>'' AND
    NOT EXISTS(SELECT 1 FROM alert_outbox o WHERE o.item_id=i.id AND o.alert_cycle=(SELECT cycle FROM low_stock_state WHERE item_id=i.id))
    """,c,tx);
    queue.Parameters.AddWithValue("item",itemId);queue.Parameters.AddWithValue("store",store);
    await queue.ExecuteNonQueryAsync();
}
app.MapGet("/api/items", async (HttpContext ctx) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT id,sku,name,quantity,COALESCE(category,''),COALESCE(barcode,''),min_stock,unit_cost,COALESCE(description,'') FROM items WHERE store_id=@store ORDER BY name LIMIT 1000", c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r = await cmd.ExecuteReaderAsync();
    var list = new List<object>();
    while (await r.ReadAsync()) list.Add(new { id=r.GetGuid(0),sku=r.GetString(1),name=r.GetString(2),quantity=r.GetInt32(3),category=r.GetString(4),barcode=r.GetString(5),minStock=r.GetInt32(6),unitCost=r.IsDBNull(7)?(decimal?)null:r.GetDecimal(7),description=r.GetString(8) });
    return Results.Ok(list);
});
app.MapPost("/api/items", async (HttpContext ctx, NewItem req) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(req.Sku) || string.IsNullOrWhiteSpace(req.Name) || req.Quantity < 0 || req.MinStock < 0 || req.UnitCost < 0) return Results.BadRequest("Invalid item");
    await using var c = await Connect();
    await using var tx = await c.BeginTransactionAsync();
    var id = Guid.NewGuid();
    await using (var cmd = new NpgsqlCommand("INSERT INTO items(id,sku,name,quantity,barcode,category,description,min_stock,unit_cost,store_id) VALUES(@id,@sku,@name,@qty,NULLIF(@barcode,''),@category,@description,@min,@cost,@store)",c,tx))
    {
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);cmd.Parameters.AddWithValue("sku",req.Sku.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());cmd.Parameters.AddWithValue("qty",req.Quantity);cmd.Parameters.AddWithValue("barcode",(req.Barcode??"").Trim());cmd.Parameters.AddWithValue("category",req.Category??"");cmd.Parameters.AddWithValue("description",req.Description??"");cmd.Parameters.AddWithValue("min",req.MinStock);cmd.Parameters.AddWithValue("cost",(object?)req.UnitCost??DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    if (req.Quantity > 0)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO movements(item_id,delta,before_qty,after_qty,note,store_id,employee_id) VALUES(@id,@delta,0,@delta,'Initial stock',@store,@emp)",c,tx);
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("delta",req.Quantity);cmd.Parameters.AddWithValue("store",who.Value.store);cmd.Parameters.AddWithValue("emp",who.Value.employee);await cmd.ExecuteNonQueryAsync();
    }
    await QueueLowStock(c,(NpgsqlTransaction)tx,id,who.Value.store);
    await tx.CommitAsync();
    return Results.Created($"/api/items/{id}",new{id});
});
app.MapPost("/api/items/{id:guid}/movements", async (HttpContext ctx, Guid id, MovementRequest req) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(req.Delta<0 && string.IsNullOrWhiteSpace(req.Note))return Results.BadRequest("Checkout reason is required.");
    if (req.Delta==0 || req.IdempotencyKey==Guid.Empty) return Results.BadRequest("Nonzero delta and idempotency key required");
    await using var c = await Connect();
    await using var tx = await c.BeginTransactionAsync();
    await using (var duplicate = new NpgsqlCommand("SELECT after_qty FROM movements WHERE idempotency_key=@key AND store_id=@store",c,tx))
    {
        duplicate.Parameters.AddWithValue("key",req.IdempotencyKey);duplicate.Parameters.AddWithValue("store",who.Value.store);
        var found = await duplicate.ExecuteScalarAsync();
        if (found is not null) return Results.Ok(new { after=(int)found,duplicate=true });
    }
    await using var select = new NpgsqlCommand("SELECT quantity FROM items WHERE id=@id AND store_id=@store FOR UPDATE",c,tx);
    select.Parameters.AddWithValue("id",id);select.Parameters.AddWithValue("store",who.Value.store);
    var current=await select.ExecuteScalarAsync();
    if (current is null) return Results.NotFound();
    var before=(int)current;
    var after=(long)before+req.Delta;
    if (after<0 || after>int.MaxValue) return Results.BadRequest("Insufficient stock or quantity overflow");
    await using (var update = new NpgsqlCommand("UPDATE items SET quantity=@qty WHERE id=@id AND store_id=@store",c,tx))
    {
        update.Parameters.AddWithValue("qty",(int)after);update.Parameters.AddWithValue("id",id);update.Parameters.AddWithValue("store",who.Value.store);await update.ExecuteNonQueryAsync();
    }
    await using (var insert = new NpgsqlCommand("INSERT INTO movements(item_id,delta,before_qty,after_qty,note,idempotency_key,store_id,employee_id) VALUES(@id,@delta,@before,@after,@note,@key,@store,@emp) ON CONFLICT (idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING",c,tx))
    {
        insert.Parameters.AddWithValue("id",id);insert.Parameters.AddWithValue("delta",req.Delta);insert.Parameters.AddWithValue("before",before);insert.Parameters.AddWithValue("after",(int)after);
        insert.Parameters.AddWithValue("note",(object?)req.Note??DBNull.Value);insert.Parameters.AddWithValue("key",req.IdempotencyKey);insert.Parameters.AddWithValue("store",who.Value.store);insert.Parameters.AddWithValue("emp",who.Value.employee);
        if (await insert.ExecuteNonQueryAsync()==0) { await tx.RollbackAsync(); return Results.Conflict("Retry request after concurrent transaction"); }
    }
    await QueueLowStock(c,(NpgsqlTransaction)tx,id,who.Value.store);
    await tx.CommitAsync();
    return Results.Ok(new { before,after });
});
app.MapGet("/api/movements", async (HttpContext ctx) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT m.id,i.sku,i.name,m.delta,m.before_qty,m.after_qty,m.created_at,m.note,m.store_id,m.employee_id FROM movements m JOIN items i ON i.id=m.item_id WHERE m.store_id=@store ORDER BY m.id DESC LIMIT 200",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    var list=new List<object>();
    while(await r.ReadAsync()) list.Add(new{id=r.GetInt64(0),sku=r.GetString(1),name=r.GetString(2),delta=r.GetInt32(3),before=r.GetInt32(4),after=r.GetInt32(5),time=r.GetDateTime(6),note=r.IsDBNull(7)?null:r.GetString(7),storeId=r.GetString(8),employeeId=r.GetString(9)});
    return Results.Ok(list);
});

app.MapGet("/api/summary", async (HttpContext ctx) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT count(*),COALESCE(sum(quantity),0),count(*) FILTER(WHERE quantity<=min_stock),COALESCE(sum(quantity*unit_cost),0) FROM items WHERE store_id=@store",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();
    return Results.Ok(new{products=r.GetInt64(0),units=r.GetInt64(1),lowStock=r.GetInt64(2),value=r.GetDecimal(3)});
});
app.MapGet("/api/items/lookup/{code}", async (HttpContext ctx, string code) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT id,sku,name,quantity FROM items WHERE store_id=@store AND (sku=@code OR barcode=@code) LIMIT 1",c);
    cmd.Parameters.AddWithValue("code",code);cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    return await r.ReadAsync()?Results.Ok(new{id=r.GetGuid(0),sku=r.GetString(1),name=r.GetString(2),quantity=r.GetInt32(3)}):Results.NotFound();
});
app.MapPut("/api/items/{id:guid}", async (HttpContext ctx, Guid id, EditItem req) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(string.IsNullOrWhiteSpace(req.Name)||string.IsNullOrWhiteSpace(req.Sku)||req.MinStock<0||req.UnitCost<0)return Results.BadRequest("Invalid item details");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("UPDATE items SET sku=@sku,name=@name,barcode=NULLIF(@barcode,''),category=@category,description=@description,min_stock=@min,unit_cost=@cost,updated_at=now() WHERE id=@id AND store_id=@store",c);
    cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);cmd.Parameters.AddWithValue("sku",req.Sku.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());
    cmd.Parameters.AddWithValue("barcode",(req.Barcode??"").Trim());cmd.Parameters.AddWithValue("category",(req.Category??"").Trim());cmd.Parameters.AddWithValue("description",(req.Description??"").Trim());
    cmd.Parameters.AddWithValue("min",req.MinStock);cmd.Parameters.AddWithValue("cost",(object?)req.UnitCost??DBNull.Value);
    var updated=await cmd.ExecuteNonQueryAsync();
    if(updated==0)return Results.NotFound();
    await using var tx=await c.BeginTransactionAsync();
    await QueueLowStock(c,(NpgsqlTransaction)tx,id,who.Value.store);
    await tx.CommitAsync();
    return Results.Ok(new{updated=true});
});

// Delivery loop runs on the API host; inventory operations never depend on Gmail availability.
var mailCts=new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(()=>mailCts.Cancel());
_ = Task.Run(async ()=>{
    while(!mailCts.IsCancellationRequested){
        try{
            if(MailConfigured()){
                await using var c=await Connect();
                await using var cmd=new NpgsqlCommand("""
                SELECT o.id,o.store_id,o.subject,o.body,a.recipients
                FROM alert_outbox o JOIN store_alert_settings a ON a.store_id=o.store_id
                WHERE o.sent_at IS NULL AND o.next_attempt<=now() AND a.enabled AND a.recipients<>''
                ORDER BY o.id LIMIT 20
                """,c);
                var pending=new List<(long id,string store,string subject,string body,string recipients)>();
                await using(var r=await cmd.ExecuteReaderAsync()){while(await r.ReadAsync())pending.Add((r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4)));}
                foreach(var job in pending){
                    try{
                        await SendMail(ParseRecipients(job.recipients),job.subject,job.body);
                        await using var done=new NpgsqlCommand("UPDATE alert_outbox SET sent_at=now() WHERE id=@id AND sent_at IS NULL",c);
                        done.Parameters.AddWithValue("id",job.id);await done.ExecuteNonQueryAsync();
                    }catch(Exception ex){
                        app.Logger.LogWarning(ex,"Low-stock email delivery failed for store {Store}",job.store);
                        await using var retry=new NpgsqlCommand("UPDATE alert_outbox SET attempts=attempts+1,next_attempt=now()+interval '5 minutes' * LEAST(attempts+1,12) WHERE id=@id",c);
                        retry.Parameters.AddWithValue("id",job.id);await retry.ExecuteNonQueryAsync();
                    }
                }
            }
        }catch(Exception ex){app.Logger.LogError(ex,"Low-stock mail worker error");}
        try{await Task.Delay(TimeSpan.FromSeconds(30),mailCts.Token);}catch(OperationCanceledException){break;}
    }
});
await using (var c=await Connect())
{
    await using var cmd=new NpgsqlCommand("""
    CREATE TABLE IF NOT EXISTS settings(key text PRIMARY KEY,value text NOT NULL);
    CREATE TABLE IF NOT EXISTS stores(id text PRIMARY KEY,name text NOT NULL,active boolean NOT NULL DEFAULT true);
    CREATE TABLE IF NOT EXISTS store_alert_settings(store_id text PRIMARY KEY REFERENCES stores(id) ON UPDATE CASCADE,enabled boolean NOT NULL DEFAULT false,recipients text NOT NULL DEFAULT '');
    CREATE TABLE IF NOT EXISTS low_stock_state(item_id uuid PRIMARY KEY,store_id text NOT NULL,is_low boolean NOT NULL DEFAULT false,cycle bigint NOT NULL DEFAULT 0);
    CREATE TABLE IF NOT EXISTS alert_outbox(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,store_id text NOT NULL,item_id uuid NOT NULL,alert_cycle bigint NOT NULL DEFAULT 0,subject text NOT NULL,body text NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),sent_at timestamptz,attempts integer NOT NULL DEFAULT 0,next_attempt timestamptz NOT NULL DEFAULT now());
    CREATE UNIQUE INDEX IF NOT EXISTS alert_outbox_item_cycle_idx ON alert_outbox(item_id,alert_cycle);
    INSERT INTO stores(id,name) SELECT 'DEFAULT','Original Store' WHERE NOT EXISTS(SELECT 1 FROM stores) ON CONFLICT DO NOTHING;
    CREATE TABLE IF NOT EXISTS employees(store_id text NOT NULL REFERENCES stores(id),id text NOT NULL,name text NOT NULL,active boolean NOT NULL DEFAULT true,PRIMARY KEY(store_id,id));
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS primary_store_id text;
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS global_admin boolean NOT NULL DEFAULT false;
    UPDATE employees SET primary_store_id=store_id WHERE primary_store_id IS NULL;
    CREATE TABLE IF NOT EXISTS employee_store_access(employee_id text NOT NULL,store_id text NOT NULL REFERENCES stores(id),PRIMARY KEY(employee_id,store_id));
    ALTER TABLE employee_store_access ADD COLUMN IF NOT EXISTS role text NOT NULL DEFAULT 'employee';
    INSERT INTO employees(store_id,id,name,primary_store_id) SELECT id,'OWNER','Original Administrator',id FROM stores WHERE NOT EXISTS(SELECT 1 FROM employees WHERE id='OWNER') ORDER BY id LIMIT 1 ON CONFLICT DO NOTHING;
    INSERT INTO employee_store_access(employee_id,store_id) SELECT id,store_id FROM employees ON CONFLICT DO NOTHING;
    UPDATE employees SET global_admin=true WHERE id='OWNER';
    CREATE TABLE IF NOT EXISTS sessions(token_hash text PRIMARY KEY,expires_at timestamptz NOT NULL);
    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS store_id text NOT NULL DEFAULT 'DEFAULT';
    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS employee_id text NOT NULL DEFAULT 'OWNER';
    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS is_admin boolean NOT NULL DEFAULT false;
    CREATE TABLE IF NOT EXISTS items(id uuid PRIMARY KEY,sku text NOT NULL UNIQUE,name text NOT NULL,quantity integer NOT NULL DEFAULT 0 CHECK(quantity>=0));
    CREATE TABLE IF NOT EXISTS movements(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,item_id uuid NOT NULL REFERENCES items(id),delta integer NOT NULL CHECK(delta<>0),before_qty integer NOT NULL,after_qty integer NOT NULL,note text,created_at timestamptz NOT NULL DEFAULT now());
    ALTER TABLE items ADD COLUMN IF NOT EXISTS store_id text NOT NULL DEFAULT 'DEFAULT' REFERENCES stores(id);
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS store_id text NOT NULL DEFAULT 'DEFAULT' REFERENCES stores(id);
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS employee_id text NOT NULL DEFAULT 'LEGACY';
    ALTER TABLE items DROP CONSTRAINT IF EXISTS items_sku_key;
    CREATE UNIQUE INDEX IF NOT EXISTS items_store_sku_idx ON items(store_id,sku);
    ALTER TABLE items ADD COLUMN IF NOT EXISTS category text NOT NULL DEFAULT '';
    ALTER TABLE items ADD COLUMN IF NOT EXISTS description text NOT NULL DEFAULT '';
    ALTER TABLE items ADD COLUMN IF NOT EXISTS barcode text;
    ALTER TABLE items ADD COLUMN IF NOT EXISTS min_stock integer NOT NULL DEFAULT 0;
    ALTER TABLE items ADD COLUMN IF NOT EXISTS unit_cost numeric(12,2);
    ALTER TABLE items ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
    DROP INDEX IF EXISTS items_barcode_idx;
    CREATE UNIQUE INDEX IF NOT EXISTS items_store_barcode_idx ON items(store_id,barcode) WHERE barcode IS NOT NULL;
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS idempotency_key uuid;
    CREATE UNIQUE INDEX IF NOT EXISTS movements_idempotency_key_idx ON movements(idempotency_key) WHERE idempotency_key IS NOT NULL;
    """,c);
    await cmd.ExecuteNonQueryAsync();
}
app.Run();
record Setup(string? SetupToken,string? Pin,string? ConfirmPin);
record Login(string? Pin,string? EmployeeId=null);
record NewItem(string Sku,string Name,int Quantity,string? Barcode="",string? Category="",string? Description="",int MinStock=0,decimal? UnitCost=null);
record MovementRequest(int Delta,string? Note,Guid IdempotencyKey);
record EditItem(string Sku,string Name,string? Barcode,string? Category,string? Description,int MinStock,decimal? UnitCost);

record EmployeeLogin(string? EmployeeId);
record SelectStore(string? StoreId);
record StoreRequest(string? Id,string? Name);
record EmployeeRequest(string? StoreId,string? Id,string? Name);

record StoreNameRequest(string? Name);
record AlertSettingsRequest(bool Enabled,string? Recipients);

record EmployeePermissions(bool GlobalAdmin,string[]? StoreAdminIds);
