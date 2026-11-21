using System.Net.Http.Headers;
using System.Text.Json;
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
    options.AddPolicy("sensitive", context => RateLimitPartition.GetFixedWindowLimiter(
        (context.Connection.RemoteIpAddress?.ToString() ?? "unknown")+":"+context.Request.Path,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
var cs = builder.Configuration.GetConnectionString("Inventory") ?? throw new Exception("Missing database connection");
var setupToken = Environment.GetEnvironmentVariable("SETUP_TOKEN") ?? throw new Exception("Missing setup token");
if (setupToken.Length < 32 || setupToken.StartsWith("replace-")) throw new Exception("Invalid setup token");
// Canonical external HTTPS origin is configured by the installing business.
// Never infer it from untrusted request Host headers or silently use someone else's domain.
var publicOriginText=Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")??"";
if(!Uri.TryCreate(publicOriginText,UriKind.Absolute,out Uri? publicOriginUri)
   || publicOriginUri.Scheme!=Uri.UriSchemeHttps
   || publicOriginUri.HostNameType!=UriHostNameType.Dns
   || !publicOriginUri.Host.Contains('.')
   || !string.IsNullOrEmpty(publicOriginUri.UserInfo)
   || publicOriginUri.AbsolutePath!="/"
   || !string.IsNullOrEmpty(publicOriginUri.Query)
   || !string.IsNullOrEmpty(publicOriginUri.Fragment)
   || !publicOriginUri.IsDefaultPort
   || publicOriginText.EndsWith("/",StringComparison.Ordinal)
   || !string.Equals(publicOriginText,publicOriginUri.GetLeftPart(UriPartial.Authority),StringComparison.Ordinal))
    throw new InvalidOperationException("PUBLIC_ORIGIN must be a canonical HTTPS URL such as https://inventory.example.com (without trailing slash, port, path, query or fragment).");
var PublicOrigin=publicOriginText;
var GoogleRedirect=PublicOrigin+"/api/google/callback";
const string GmailScope="https://www.googleapis.com/auth/gmail.send";
var googleCredentials=new GoogleOAuthCredentials(Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")??"",Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET")??"");
var keyText=Environment.GetEnvironmentVariable("GOOGLE_TOKEN_KEY")??"";
byte[]? googleKey=null;
try{if(keyText.Length==64)googleKey=Convert.FromHexString(keyText);}catch(FormatException){}
bool GoogleReady()=>googleCredentials.ClientId.Length>0&&googleCredentials.ClientSecret.Length>0&&googleKey is {Length:32};
string EncryptToken(string value){
    if(googleKey is null)throw new InvalidOperationException("Token encryption not configured");
    var nonce=RandomNumberGenerator.GetBytes(12);var plain=Encoding.UTF8.GetBytes(value);var cipher=new byte[plain.Length];var tag=new byte[16];
    using(var aes=new AesGcm(googleKey,16))aes.Encrypt(nonce,plain,cipher,tag);
    return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
}
string DecryptToken(string value){
    if(googleKey is null)throw new InvalidOperationException("Token encryption not configured");
    var bytes=Convert.FromBase64String(value);var plain=new byte[bytes.Length-28];
    using(var aes=new AesGcm(googleKey,16))aes.Decrypt(bytes.AsSpan(0,12),bytes.AsSpan(28),bytes.AsSpan(12,16),plain);
    return Encoding.UTF8.GetString(plain);
}
using var googleHttp=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
// Password and email verification replace legacy shared PIN / employee-ID-only authentication.
// Password hashes include a random per-user salt and use PBKDF2-HMAC-SHA256 (600k iterations).
static bool PasswordValid(string? p)=>p is {Length: >=8 and <=128}
    &&p.Any(char.IsUpper)&&p.Any(char.IsLower)&&p.Any(char.IsDigit)
    &&p.Any(c=>!char.IsLetterOrDigit(c))&&!p.Any(char.IsControl);
static string PasswordHash(string password){
    var salt=RandomNumberGenerator.GetBytes(24);
    return "pbkdf2-sha256:600000:"+Convert.ToHexString(salt)+":"+
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(password,salt,600_000,HashAlgorithmName.SHA256,32));
}
static bool PasswordMatches(string? password,string? stored){
    if(string.IsNullOrEmpty(password)||string.IsNullOrEmpty(stored))return false;
    try{
        var parts=stored.Split(':');
        if(parts.Length!=4||parts[0]!="pbkdf2-sha256"||parts[1]!="600000")return false;
        var expected=Convert.FromHexString(parts[3]);
        var actual=Rfc2898DeriveBytes.Pbkdf2(password,Convert.FromHexString(parts[2]),600_000,HashAlgorithmName.SHA256,32);
        return CryptographicOperations.FixedTimeEquals(actual,expected);
    }catch(Exception ex)when(ex is FormatException or ArgumentException){return false;}
}
static bool EmailPairValid(string? email,string? confirmEmail)
    =>!string.IsNullOrWhiteSpace(email)&&email.Length<=254
       &&string.Equals(email.Trim(),confirmEmail?.Trim(),StringComparison.OrdinalIgnoreCase)
       &&System.Net.Mail.MailAddress.TryCreate(email.Trim(),out var address)
       &&string.Equals(address.Address,email.Trim(),StringComparison.OrdinalIgnoreCase);
static string NewAuthCode()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
const int SessionIdleMinutes=15;
async Task<NpgsqlConnection> Connect() { var c = new NpgsqlConnection(cs); await c.OpenAsync(); return c; }
static string CsvField(string? raw){
    var value=raw??"";
    // Escape spreadsheet formulas supplied in SKU/name/category/description.
    var trimmed=value.TrimStart(' ','\t','\r','\n');
    if(trimmed.Length>0&&"=+-@".Contains(trimmed[0]))value="'"+value;
    return "\"" + value.Replace("\"","\"\"") + "\"";
}
static IResult CsvFile(string content,string filename){
    var bytes=Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray();
    return Results.File(bytes,"text/csv; charset=utf-8",filename);
}
async Task WriteAdminAudit(NpgsqlConnection conn,NpgsqlTransaction? tx,string actor,string? store,string action,string target,string details=""){
    await using var cmd=new NpgsqlCommand("INSERT INTO admin_audit(actor_employee_id,store_id,action,target,details) VALUES(@actor,@store,@action,@target,@details)",conn,tx);
    cmd.Parameters.AddWithValue("actor",actor);
    cmd.Parameters.Add("store",NpgsqlTypes.NpgsqlDbType.Text).Value=(object?)store??DBNull.Value;
    cmd.Parameters.AddWithValue("action",action);
    cmd.Parameters.AddWithValue("target",target);
    cmd.Parameters.AddWithValue("details",details);
    await cmd.ExecuteNonQueryAsync();
}

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

const int MaxFailedSignIns=5;
// Per-IP, per-login-type counters are persisted so API restarts do not clear lockouts.
// Successful authentication deletes the record; successful requests never increment it.
string SignInAttemptKey(HttpContext ctx,string kind)=>Hash(kind+":"+(ctx.Connection.RemoteIpAddress?.ToString()??"unknown"));
async Task<int> RemainingSignInLockSeconds(HttpContext ctx,string kind){
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT GREATEST(0,CEIL(EXTRACT(EPOCH FROM (blocked_until-now()))))::integer
        FROM auth_login_failures WHERE attempt_key=@key AND blocked_until>now()
        """,c);
    cmd.Parameters.AddWithValue("key",SignInAttemptKey(ctx,kind));
    return (int?)await cmd.ExecuteScalarAsync()??0;
}
IResult SignInLocked(HttpContext ctx,int seconds){
    ctx.Response.Headers["Retry-After"]=seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    return Results.Json(new{error="Too many failed sign-in attempts.",retryAfterSeconds=seconds},statusCode:429);
}
async Task<IResult?> CheckSignInLock(HttpContext ctx,string kind){
    var remaining=await RemainingSignInLockSeconds(ctx,kind);
    return remaining>0?SignInLocked(ctx,remaining):null;
}
async Task<IResult> RejectedSignIn(HttpContext ctx,string kind){
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        INSERT INTO auth_login_failures(attempt_key,failures,last_failure,blocked_until)
        VALUES(@key,1,now(),NULL)
        ON CONFLICT(attempt_key) DO UPDATE SET
          failures=CASE WHEN auth_login_failures.last_failure < now()-interval '5 minutes'
                        THEN 1 ELSE auth_login_failures.failures+1 END,
          last_failure=now(),
          blocked_until=CASE WHEN auth_login_failures.last_failure < now()-interval '5 minutes' THEN NULL
                        WHEN auth_login_failures.failures+1>=@threshold THEN now()+interval '5 minutes'
                        ELSE NULL END
        RETURNING GREATEST(0,CEIL(EXTRACT(EPOCH FROM (blocked_until-now()))))::integer
        """,c);
    cmd.Parameters.AddWithValue("key",SignInAttemptKey(ctx,kind));
    cmd.Parameters.AddWithValue("threshold",MaxFailedSignIns);
    var seconds=(int)(await cmd.ExecuteScalarAsync()??0);
    return seconds>0?SignInLocked(ctx,seconds):Results.Unauthorized();
}
async Task SignInSucceeded(HttpContext ctx,string kind){
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("DELETE FROM auth_login_failures WHERE attempt_key=@key",c);
    cmd.Parameters.AddWithValue("key",SignInAttemptKey(ctx,kind));
    await cmd.ExecuteNonQueryAsync();
}
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// The installation token is a one-time bootstrap credential for the original OWNER.
// No shared PIN remains accepted for login after the password-authentication migration.
static bool ThemeValid(string? theme)=>theme is "mint" or "purple" or "ocean" or "ember" or "rose" or "glacier" or "slate" or "forest" or "gold" or "crimson";
app.MapGet("/api/auth/status", async ()=>{
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employees WHERE id=(SELECT value FROM settings WHERE key='owner_employee_id') AND active AND global_admin AND password_hash IS NOT NULL)",c);
    var initialized=(bool)(await cmd.ExecuteScalarAsync()??false);
    await using var themeCmd=new NpgsqlCommand("SELECT value FROM settings WHERE key='login_theme'",c);
    var loginTheme=(string?)await themeCmd.ExecuteScalarAsync();
    return Results.Ok(new{initialized,loginMethod="password",loginTheme=ThemeValid(loginTheme)?loginTheme:"mint"});
});
app.MapPost("/api/auth/setup",async (Setup request)=>{
    if(!Equal(Hash(request.SetupToken??""),Hash(setupToken)))return Results.Unauthorized();
    if(!EmailPairValid(request.Email,request.ConfirmEmail))return Results.BadRequest("Enter your email address twice; both entries must match.");
    if(!PasswordValid(request.Password)||request.Password!=request.ConfirmPassword)
        return Results.BadRequest("Passwords must match and contain at least 8 characters, uppercase, lowercase, a number and a symbol.");
    var email=request.Email!.Trim().ToLowerInvariant();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        UPDATE employees SET email=@email,password_hash=@hash,password_updated_at=now(),
            bootstrap_provisional=true
        WHERE id=(SELECT value FROM settings WHERE key='owner_employee_id') AND active AND global_admin AND password_hash IS NULL
        """,c);
    cmd.Parameters.AddWithValue("email",email);
    cmd.Parameters.AddWithValue("hash",PasswordHash(request.Password!));
    try{
        return await cmd.ExecuteNonQueryAsync()==1
            ?Results.Ok(new{created=true,emailVerificationPending=true})
            :Results.Conflict("Initial administrator already configured.");
    }catch(PostgresException ex)when(ex.SqlState=="23505"){return Results.Conflict("This email address is already in use.");}
}).RequireRateLimiting("sensitive");
// Retired endpoint: fail closed; accepting the old PIN would bypass per-user passwords.
app.MapPost("/api/auth/login",()=>Results.StatusCode(StatusCodes.Status410Gone));
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
    if(!Regex.IsMatch(token,@"^[0-9A-Fa-f]{64}$"))return null;
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT sess.store_id,sess.employee_id,sess.is_admin FROM sessions sess
        JOIN employees e ON e.id=sess.employee_id AND e.active AND e.password_hash IS NOT NULL
        WHERE sess.token_hash=@hash AND sess.expires_at>now()
          AND (e.email_verified_at IS NOT NULL OR (e.id=(SELECT value FROM settings WHERE key='owner_employee_id') AND e.global_admin AND e.bootstrap_provisional))
          AND ((sess.is_admin AND e.global_admin) OR
            EXISTS(SELECT 1 FROM employee_store_access a JOIN stores st ON st.id=a.store_id
              WHERE a.employee_id=sess.employee_id AND a.store_id=sess.store_id AND st.active))
        """,c);
    cmd.Parameters.AddWithValue("hash",Hash(token));
    await using var reader=await cmd.ExecuteReaderAsync();
    if(!await reader.ReadAsync())return null;
    var store=reader.GetString(0);var employee=reader.GetString(1);var globalSession=reader.GetBoolean(2);
    await reader.CloseAsync();
    await using var roleCmd=new NpgsqlCommand("""
        SELECT COALESCE((SELECT global_admin FROM employees WHERE id=@employee LIMIT 1),false),
        COALESCE((SELECT role='store_admin' FROM employee_store_access
                  WHERE employee_id=@employee AND store_id=@store),false)
        """,c);
    roleCmd.Parameters.AddWithValue("employee",employee);roleCmd.Parameters.AddWithValue("store",store);
    await using var roles=await roleCmd.ExecuteReaderAsync();
    await roles.ReadAsync();
    var global=globalSession&&roles.GetBoolean(0);
    return(store,employee,global||roles.GetBoolean(1),global);
}
app.MapGet("/api/auth/me",async (HttpContext ctx)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT name,email,email_verified_at IS NOT NULL,theme FROM employees
        WHERE id=@employee ORDER BY CASE WHEN primary_store_id=@store THEN 0 ELSE 1 END LIMIT 1
        """,c);
    cmd.Parameters.AddWithValue("employee",who.Value.employee);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var reader=await cmd.ExecuteReaderAsync();
    if(!await reader.ReadAsync())return Results.Unauthorized();
    return Results.Ok(new{
        storeId=who.Value.store,employeeId=who.Value.employee,
        employeeName=reader.GetString(0),email=reader.IsDBNull(1)?null:reader.GetString(1),
        emailVerified=reader.GetBoolean(2),theme=ThemeValid(reader.GetString(3))?reader.GetString(3):"mint",isAdmin=who.Value.admin,isGlobalAdmin=who.Value.globalAdmin
    });
});
// Preferences are persisted server-side per employee, independent of browser/device.
app.MapPut("/api/auth/theme",async (HttpContext ctx,ThemeChoice req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!ThemeValid(req.Theme))return Results.BadRequest("Unknown theme.");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("UPDATE employees SET theme=@theme WHERE id=@id AND active",c);
    cmd.Parameters.AddWithValue("theme",req.Theme!);cmd.Parameters.AddWithValue("id",who.Value.employee);
    return await cmd.ExecuteNonQueryAsync()==1?Results.Ok(new{theme=req.Theme}):Results.NotFound();
});
app.MapPut("/api/admin/theme/login",async (HttpContext ctx,ThemeChoice req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(!ThemeValid(req.Theme))return Results.BadRequest("Unknown theme.");
    await using var c=await Connect();await using var tx=await c.BeginTransactionAsync();
    await using(var cmd=new NpgsqlCommand("""
        INSERT INTO settings(key,value) VALUES('login_theme',@theme)
        ON CONFLICT(key) DO UPDATE SET value=excluded.value
        """,c,tx)){
        cmd.Parameters.AddWithValue("theme",req.Theme!);await cmd.ExecuteNonQueryAsync();
    }
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,null,"theme.login_changed","login",req.Theme!);
    await tx.CommitAsync();return Results.Ok(new{loginTheme=req.Theme});
});

app.MapPost("/api/auth/employee-login",async (HttpContext ctx,EmployeeLogin req)=>{
    var locked=await CheckSignInLock(ctx,"employee");
    if(locked is not null)return locked;
    if(string.IsNullOrWhiteSpace(req.EmployeeId)||string.IsNullOrEmpty(req.Password))
        return await RejectedSignIn(ctx,"employee");
    var employeeId=req.EmployeeId.Trim();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT e.password_hash,e.email_verified_at IS NOT NULL,e.bootstrap_provisional,e.global_admin,
            (SELECT a.store_id FROM employee_store_access a JOIN stores st ON st.id=a.store_id AND st.active
             WHERE a.employee_id=e.id
             ORDER BY CASE WHEN a.store_id=e.primary_store_id THEN 0 ELSE 1 END,a.store_id LIMIT 1)
        FROM employees e WHERE e.id=@employee AND e.active
        """,c);
    cmd.Parameters.AddWithValue("employee",employeeId);
    string? passwordHash=null;bool verified=false,provisional=false,global=false;string? store=null;
    await using(var reader=await cmd.ExecuteReaderAsync()){
        if(await reader.ReadAsync()){
            passwordHash=reader.IsDBNull(0)?null:reader.GetString(0);
            verified=reader.GetBoolean(1);provisional=reader.GetBoolean(2);global=reader.GetBoolean(3);
            store=reader.IsDBNull(4)?null:reader.GetString(4);
        }
    }
    if(passwordHash is null&&store is not null && !provisional)
        return Results.Json(new{setupRequired=true,message="This employee ID requires first-time account setup."},statusCode:409);
    if(!PasswordMatches(req.Password,passwordHash)||store is null||(!verified&&!(provisional&&global)))
        return await RejectedSignIn(ctx,"employee");
    var token=NewAuthCode();
    await using var insert=new NpgsqlCommand("""
        INSERT INTO sessions(token_hash,expires_at,last_seen_at,store_id,employee_id,is_admin)
        VALUES(@hash,now()+interval '15 minutes',now(),@store,@employee,@admin)
        """,c);
    insert.Parameters.AddWithValue("hash",Hash(token));insert.Parameters.AddWithValue("store",store);
    insert.Parameters.AddWithValue("employee",employeeId);insert.Parameters.AddWithValue("admin",global);
    await insert.ExecuteNonQueryAsync();
    await SignInSucceeded(ctx,"employee");
    return Results.Ok(new{token,expiresInMinutes=SessionIdleMinutes});
}).RequireRateLimiting("sensitive");
// Only explicit user activity renews the 15-minute server-side session.
// Background 20-second inventory polling does not keep an unattended session alive.
app.MapPost("/api/auth/activity",async (HttpContext ctx)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    var bearer=ctx.Request.Headers.Authorization.ToString()[7..].Trim();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        UPDATE sessions SET last_seen_at=now(),expires_at=now()+interval '15 minutes'
        WHERE token_hash=@hash AND expires_at>now()
        """,c);
    cmd.Parameters.AddWithValue("hash",Hash(bearer));
    return await cmd.ExecuteNonQueryAsync()==1?Results.Ok(new{renewed=true}):Results.Unauthorized();
});
// Self-service employee activation: the administrator only creates the employee ID.
// Email possession is proven by one single-use link before credentials become active.
app.MapPost("/api/auth/start-activation",async (HttpContext ctx,ActivateAccount req)=>{
    if(!Regex.IsMatch(req.EmployeeId??"", @"^[A-Za-z0-9_-]{2,24}$"))
        return Results.BadRequest("Enter a valid employee ID.");
    if(!EmailPairValid(req.Email,req.ConfirmEmail))
        return Results.BadRequest("Email addresses must both be valid and match.");
    if(!PasswordValid(req.Password)||req.Password!=req.ConfirmPassword)
        return Results.BadRequest("Passwords must match and contain 8+ characters, uppercase, lowercase, a number and a symbol.");
    if(!await AccountMailReady())return Results.Problem("An administrator must connect a company Gmail or SMTP sender before staff can activate their accounts.",statusCode:503);
    var email=req.Email!.Trim().ToLowerInvariant();
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var find=new NpgsqlCommand("""
        SELECT password_hash,global_admin FROM employees
        WHERE id=@id AND active AND EXISTS(SELECT 1 FROM employee_store_access WHERE employee_id=@id)
        FOR UPDATE
        """,c,tx);
    find.Parameters.AddWithValue("id",req.EmployeeId!);
    string? currentHash=null;bool isGlobal=false,found=false;
    await using(var r=await find.ExecuteReaderAsync()){
        if(await r.ReadAsync()){found=true;currentHash=r.IsDBNull(0)?null:r.GetString(0);isGlobal=r.GetBoolean(1);}
    }
    if(!found)return Results.BadRequest("This employee ID is unavailable. Contact your administrator.");
    if(isGlobal&&currentHash is null)return Results.BadRequest("The original OWNER must complete installation-token setup first.");
    if(currentHash is not null)return Results.Conflict("This account is already active. Use Forgot password instead.");
    await using(var dup=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employees WHERE lower(email)=@email AND id<>@id)",c,tx)){
        dup.Parameters.AddWithValue("email",email);dup.Parameters.AddWithValue("id",req.EmployeeId!);
        if((bool)(await dup.ExecuteScalarAsync()??false))return Results.Conflict("That email address is already associated with another employee.");
    }
    string raw;
    try{raw=await CreateAccountEmailToken(c,(NpgsqlTransaction)tx,req.EmployeeId!,email,"activate",PasswordHash(req.Password!),1440);}
    catch(InvalidOperationException ex){return Results.Conflict(ex.Message);}
    var link=(Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")??"")+"/?authToken="+raw;
    try{await SendAccountEmail(email,"Verify your Inventory Manager account",
        "Your employee ID: "+req.EmployeeId+"\n\nVerify this email and activate your account:\n"+link+
        "\n\nThis link expires in 24 hours and can be used only once. If you did not request this, ignore it.");}
    catch(Exception ex){app.Logger.LogWarning(ex,"Employee activation email could not be sent");return Results.Problem("Account verification email could not be sent. Ask an administrator to check the global mail sender.",statusCode:503);}
    await tx.CommitAsync();
    return Results.Ok(new{sent=true,message="Check your email for a verification link."});
}).RequireRateLimiting("sensitive");

// Password recovery only works for the address already verified for that employee.
// Identical responses prevent outsiders from learning who is registered.
app.MapPost("/api/auth/forgot-password",async (HttpContext ctx,ForgotPassword req)=>{
    const string message="If this employee ID has a verified email address, a password reset link will be sent.";
    if(!Regex.IsMatch(req.EmployeeId??"", @"^[A-Za-z0-9_-]{2,24}$"))return Results.Ok(new{message});
    if(!await AccountMailReady())return Results.Problem("Password reset email is temporarily unavailable. Contact an administrator.",statusCode:503);
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var find=new NpgsqlCommand("""
        SELECT email FROM employees WHERE id=@id AND active
          AND password_hash IS NOT NULL AND email_verified_at IS NOT NULL FOR UPDATE
        """,c,tx);
    find.Parameters.AddWithValue("id",req.EmployeeId!);
    var email=(string?)await find.ExecuteScalarAsync();
    if(string.IsNullOrEmpty(email))return Results.Ok(new{message});
    try{
        var raw=await CreateAccountEmailToken(c,(NpgsqlTransaction)tx,req.EmployeeId!,email,"reset",null,30);
        var link=(Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")??"")+"/?authToken="+raw;
        await SendAccountEmail(email,"Reset your Inventory Manager password",
            "A password reset was requested for employee ID "+req.EmployeeId+".\n\nTo set a new password, open:\n"+
            link+"\n\nThis link expires in 30 minutes and works only once. If you did not request it, ignore this email.");
        await tx.CommitAsync();
    }catch(Exception ex){app.Logger.LogWarning(ex,"Employee password reset email could not be sent or was throttled");}
    return Results.Ok(new{message});
}).RequireRateLimiting("sensitive");

// The one-time owner bootstrap may predate mail configuration. After the global
// sender is connected, its password-authenticated owner confirms mailbox ownership.
app.MapPost("/api/auth/send-verification",async (HttpContext ctx)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    if(!await AccountMailReady())return Results.Problem("Configure the global Gmail/SMTP sender before verifying the address.",statusCode:503);
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var find=new NpgsqlCommand("""
        SELECT email,email_verified_at FROM employees WHERE id=@id AND active FOR UPDATE
        """,c,tx);
    find.Parameters.AddWithValue("id",who.Value.employee);
    string? email=null;bool alreadyVerified=false;
    await using(var r=await find.ExecuteReaderAsync()){
        if(await r.ReadAsync()){email=r.IsDBNull(0)?null:r.GetString(0);alreadyVerified=!r.IsDBNull(1);}
    }
    if(email is null)return Results.BadRequest("No account email is configured.");
    if(alreadyVerified)return Results.Ok(new{verified=true});
    string raw;
    try{raw=await CreateAccountEmailToken(c,(NpgsqlTransaction)tx,who.Value.employee,email,"verify",null,1440);}
    catch(InvalidOperationException ex){return Results.Conflict(ex.Message);}
    var link=(Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")??"")+"/?authToken="+raw;
    try{await SendAccountEmail(email,"Verify your Inventory Manager email",
        "Confirm the email address for employee ID "+who.Value.employee+":\n"+link+
        "\n\nThis single-use link expires in 24 hours.");}
    catch(Exception ex){app.Logger.LogWarning(ex,"Account verification email failed");return Results.Problem("Verification email could not be sent.",statusCode:503);}
    await tx.CommitAsync();
    return Results.Ok(new{sent=true});
}).RequireRateLimiting("sensitive");

// Token details are only available to the holder of the random emailed token.
// No secrets are returned and the token is not consumed by previewing it.
app.MapPost("/api/auth/token-info",async (AuthEmailToken req)=>{
    if(!Regex.IsMatch(req.Token??"",@"^[0-9A-Fa-f]{64}$"))return Results.BadRequest("Invalid or expired link.");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT employee_id,purpose,expires_at FROM auth_email_tokens
        WHERE token_hash=@hash AND expires_at>now()
        """,c);
    cmd.Parameters.AddWithValue("hash",Hash(req.Token!));
    await using var r=await cmd.ExecuteReaderAsync();
    if(!await r.ReadAsync())return Results.BadRequest("This link has expired or has already been used.");
    return Results.Ok(new{employeeId=r.GetString(0),purpose=r.GetString(1),expiresAt=r.GetDateTime(2)});
}).RequireRateLimiting("sensitive");

app.MapPost("/api/auth/change-email",async (HttpContext ctx,ChangeEmail req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!EmailPairValid(req.Email,req.ConfirmEmail))return Results.BadRequest("New email and confirmation must match and be valid.");
    if(!await AccountMailReady())return Results.Problem("Email sender is not configured.",statusCode:503);
    var email=req.Email!.Trim().ToLowerInvariant();
    await using var c=await Connect();await using var tx=await c.BeginTransactionAsync();
    string? oldEmail,storedPassword;
    await using(var cmd=new NpgsqlCommand("SELECT email,password_hash FROM employees WHERE id=@id AND active FOR UPDATE",c,tx)){
        cmd.Parameters.AddWithValue("id",who.Value.employee);
        await using var reader=await cmd.ExecuteReaderAsync();
        if(!await reader.ReadAsync())return Results.Unauthorized();
        oldEmail=reader.IsDBNull(0)?null:reader.GetString(0);
        storedPassword=reader.IsDBNull(1)?null:reader.GetString(1);
    }
    if(!PasswordMatches(req.Password,storedPassword))return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(string.Equals(email,oldEmail,StringComparison.OrdinalIgnoreCase))return Results.BadRequest("This is already your email.");
    await using(var check=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employees WHERE lower(email)=@email AND id<>@id)",c,tx)){
        check.Parameters.AddWithValue("email",email);check.Parameters.AddWithValue("id",who.Value.employee);
        if((bool)(await check.ExecuteScalarAsync()??false))return Results.Conflict("Email is already in use.");
    }
    string raw;
    try{raw=await CreateAccountEmailToken(c,(NpgsqlTransaction)tx,who.Value.employee,email,"change_email",null,30);}
    catch(InvalidOperationException ex){return Results.Conflict(ex.Message);}
    var link=(Environment.GetEnvironmentVariable("PUBLIC_ORIGIN")??"")+"/?authToken="+raw;
    try{await SendAccountEmail(email,"Confirm your new Inventory Manager email",
      "Confirm the new email for employee "+who.Value.employee+":\n"+link+"\n\nThis link expires in 30 minutes.");}
    catch(Exception ex){app.Logger.LogWarning(ex,"Email change link delivery failed");return Results.Problem("Unable to send verification. Existing email unchanged.",statusCode:503);}
    await tx.CommitAsync();return Results.Ok(new{sent=true,message="Check your new email for a confirmation link. Old email stays active until confirmed."});
}).RequireRateLimiting("sensitive");

// Atomic consumption: the token cannot be used again even if two tabs submit together.
app.MapPost("/api/auth/complete-token",async (CompleteAuthToken req)=>{
    if(!Regex.IsMatch(req.Token??"",@"^[0-9A-Fa-f]{64}$"))return Results.BadRequest("Invalid or expired link.");
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var find=new NpgsqlCommand("""
        SELECT employee_id,purpose,email,pending_password_hash FROM auth_email_tokens
        WHERE token_hash=@hash AND expires_at>now() FOR UPDATE
        """,c,tx);
    find.Parameters.AddWithValue("hash",Hash(req.Token!));
    string employee,purpose,email;string? pendingHash;
    await using(var r=await find.ExecuteReaderAsync()){
        if(!await r.ReadAsync())return Results.BadRequest("This link has expired or has already been used.");
        employee=r.GetString(0);purpose=r.GetString(1);email=r.GetString(2);
        pendingHash=r.IsDBNull(3)?null:r.GetString(3);
    }
    if(purpose=="reset"&&(!PasswordValid(req.Password)||req.Password!=req.ConfirmPassword))
        return Results.BadRequest("Passwords must match and contain at least 8 characters, uppercase, lowercase, a number and a symbol.");
    string sql=purpose switch{
        "activate"=>"""
            UPDATE employees SET email=@email,email_verified_at=now(),password_hash=@password,
            password_updated_at=now(),bootstrap_provisional=false
            WHERE id=@id AND active AND password_hash IS NULL
            """,
        "verify"=>"""
            UPDATE employees SET email_verified_at=now(),bootstrap_provisional=false
            WHERE id=@id AND active AND lower(email)=@email AND password_hash IS NOT NULL
            """,
        "reset"=>"""
            UPDATE employees SET password_hash=@password,password_updated_at=now()
            WHERE id=@id AND active AND lower(email)=@email AND email_verified_at IS NOT NULL
            """,
        "change_email"=>"""
            UPDATE employees SET email=@email,email_verified_at=now(),bootstrap_provisional=false
            WHERE id=@id AND active AND password_hash IS NOT NULL
            """,
        _=>throw new InvalidOperationException("Unknown email token purpose")
    };
    if(purpose=="activate"&&string.IsNullOrWhiteSpace(pendingHash))return Results.BadRequest("Invalid activation link.");
    await using var update=new NpgsqlCommand(sql,c,tx);
    update.Parameters.AddWithValue("id",employee);
    update.Parameters.AddWithValue("email",email);
    update.Parameters.AddWithValue("password",purpose=="activate"?pendingHash!:purpose=="reset"?PasswordHash(req.Password!):"");
    try{
        if(await update.ExecuteNonQueryAsync()!=1)return Results.BadRequest("This account is no longer eligible for this link.");
    }catch(PostgresException ex)when(ex.SqlState=="23505"){return Results.Conflict("That email address is already registered.");}
    await using var clearTokens=new NpgsqlCommand("DELETE FROM auth_email_tokens WHERE employee_id=@id",c,tx);
    clearTokens.Parameters.AddWithValue("id",employee);await clearTokens.ExecuteNonQueryAsync();
    if(purpose=="reset"||purpose=="change_email"){
        await using var clearSessions=new NpgsqlCommand("DELETE FROM sessions WHERE employee_id=@id",c,tx);
        clearSessions.Parameters.AddWithValue("id",employee);await clearSessions.ExecuteNonQueryAsync();
    }
    if(purpose=="change_email")
        await WriteAdminAudit(c,(NpgsqlTransaction)tx,employee,null,"account.email_changed",employee,"Verified new email address");
    await tx.CommitAsync();
    return Results.Ok(new{completed=true,purpose,message=purpose=="reset"?"Password changed. Sign in with your new password.":purpose=="change_email"?"Email changed. Sign in again.":"Email verified. You may now sign in."});
}).RequireRateLimiting("sensitive");
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
        if(!who.Value.globalAdmin && !(bool)(await check.ExecuteScalarAsync()??false))return Results.StatusCode(StatusCodes.Status403Forbidden);
        if(who.Value.globalAdmin){await using var exists=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM stores WHERE id=@store AND active)",c);exists.Parameters.AddWithValue("store",req.StoreId??"");if(!(bool)(await exists.ExecuteScalarAsync()??false))return Results.StatusCode(StatusCodes.Status403Forbidden);}
    }
    var auth=ctx.Request.Headers.Authorization.ToString()[7..].Trim();
    await using var cmd=new NpgsqlCommand("UPDATE sessions SET store_id=@store WHERE token_hash=@hash AND employee_id=@employee AND expires_at>now()",c);
    cmd.Parameters.AddWithValue("store",req.StoreId!);cmd.Parameters.AddWithValue("hash",Hash(auth));cmd.Parameters.AddWithValue("employee",who.Value.employee);
    return await cmd.ExecuteNonQueryAsync()==1?Results.Ok(new{storeId=req.StoreId}):Results.Unauthorized();
});
app.MapGet("/api/admin/stores",async (HttpContext ctx)=>{
    if((await Identity(ctx))?.globalAdmin!=true)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT id,name,active FROM stores ORDER BY id",c);
    await using var r=await cmd.ExecuteReaderAsync();var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{id=r.GetString(0),name=r.GetString(1),active=r.GetBoolean(2)});
    return Results.Ok(list);
});
app.MapPost("/api/admin/stores",async (HttpContext ctx,StoreRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(!Regex.IsMatch(req.Id??"",@"^[A-Za-z0-9_-]{2,24}$")||string.IsNullOrWhiteSpace(req.Name))return Results.BadRequest("Invalid store details");
    await using var c=await Connect();await using var tx=await c.BeginTransactionAsync();
    await using var cmd=new NpgsqlCommand("INSERT INTO stores(id,name) VALUES(@id,@name)",c,tx);
    cmd.Parameters.AddWithValue("id",req.Id.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());
    await cmd.ExecuteNonQueryAsync();
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,req.Id!.Trim(),"store.created",req.Id!.Trim(),req.Name!.Trim());
    await tx.CommitAsync();return Results.Ok(new{created=true});
});
app.MapPut("/api/admin/stores/{id}/name",async (HttpContext ctx,string id,StoreNameRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    var name=req.Name?.Trim();
    if(string.IsNullOrWhiteSpace(name)||name.Length>100)return Results.BadRequest("Location name must be 1–100 characters.");
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var cmd=new NpgsqlCommand("UPDATE stores SET name=@name WHERE id=@id",c,tx);
    cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("name",name);
    if(await cmd.ExecuteNonQueryAsync()!=1)return Results.NotFound();
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,id,"store.updated",id,"Store name changed");
    await tx.CommitAsync();return Results.Ok(new{updated=true});
});
app.MapPut("/api/admin/stores/{id}",async (HttpContext ctx,string id,StoreRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
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
        foreach(var table in new[]{"items","movements","employees","employee_store_access","sessions","low_stock_state","alert_outbox","store_alert_settings","google_store_accounts","google_oauth_states","repair_tickets","repair_parts","repair_payments","repair_events"}){
            await using var move=new NpgsqlCommand($"UPDATE {table} SET store_id=@newId WHERE store_id=@oldId",c,tx);
            move.Parameters.AddWithValue("newId",newId);move.Parameters.AddWithValue("oldId",id);
            await move.ExecuteNonQueryAsync();
        }
        await using(var oldAudit=new NpgsqlCommand("UPDATE admin_audit SET store_id=@newId WHERE store_id=@oldId",c,tx)){
            oldAudit.Parameters.AddWithValue("newId",newId);oldAudit.Parameters.AddWithValue("oldId",id);
            await oldAudit.ExecuteNonQueryAsync();
        }
        await using(var primary=new NpgsqlCommand("UPDATE employees SET primary_store_id=@newId WHERE primary_store_id=@oldId",c,tx)){
            primary.Parameters.AddWithValue("newId",newId);primary.Parameters.AddWithValue("oldId",id);await primary.ExecuteNonQueryAsync();
        }
        await using(var remove=new NpgsqlCommand("DELETE FROM stores WHERE id=@oldId",c,tx)){
            remove.Parameters.AddWithValue("oldId",id);await remove.ExecuteNonQueryAsync();
        }
    }
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,newId,"store.updated",newId,id==newId?"Store name changed":"Store ID changed from "+id);
    await tx.CommitAsync();return Results.Ok(new{id=newId,name});
});
app.MapGet("/api/admin/employees",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();await using var cmd=new NpgsqlCommand("SELECT primary_store_id,id,name,active,global_admin FROM employees WHERE @global OR id IN(SELECT employee_id FROM employee_store_access WHERE store_id=@store) ORDER BY primary_store_id,id",c);
    cmd.Parameters.AddWithValue("global",who.Value.globalAdmin);cmd.Parameters.AddWithValue("store",who.Value.store);await using var r=await cmd.ExecuteReaderAsync();var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{storeId=r.GetString(0),id=r.GetString(1),name=r.GetString(2),active=r.GetBoolean(3),globalAdmin=r.GetBoolean(4)});
    return Results.Ok(list);
});
app.MapPost("/api/admin/employees",async (HttpContext ctx,EmployeeRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.StatusCode(StatusCodes.Status403Forbidden);if(!who.Value.globalAdmin&&who.Value.store!=req.StoreId)return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(!Regex.IsMatch(req.Id??"",@"^[A-Za-z0-9_-]{2,24}$")||string.IsNullOrWhiteSpace(req.StoreId)||string.IsNullOrWhiteSpace(req.Name))return Results.BadRequest("Invalid employee details");
    await using var c=await Connect();
    await using(var duplicate=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employees WHERE id=@id)",c)){duplicate.Parameters.AddWithValue("id",req.Id.Trim());if((bool)(await duplicate.ExecuteScalarAsync()??false))return Results.Conflict("Employee ID already exists. IDs must be unique across all stores.");}
    await using var tx=await c.BeginTransactionAsync();
    await using var cmd=new NpgsqlCommand("INSERT INTO employees(store_id,primary_store_id,id,name) VALUES(@store,@store,@id,@name)",c,tx);
    cmd.Parameters.AddWithValue("store",req.StoreId.Trim());cmd.Parameters.AddWithValue("id",req.Id.Trim());cmd.Parameters.AddWithValue("name",req.Name.Trim());
    await cmd.ExecuteNonQueryAsync();
    await using var access=new NpgsqlCommand("INSERT INTO employee_store_access(employee_id,store_id) VALUES(@employee,@store)",c,tx);
    access.Parameters.AddWithValue("employee",req.Id.Trim());access.Parameters.AddWithValue("store",req.StoreId.Trim());await access.ExecuteNonQueryAsync();
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,req.StoreId!.Trim(),"employee.created",req.Id!.Trim(),req.Name!.Trim());
    await tx.CommitAsync();return Results.Ok(new{created=true});
});

// Display-name updates do not change stable employee IDs or transaction attribution.
app.MapPut("/api/admin/employees/{id}/name",async (HttpContext ctx,string id,EmployeeNameRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.admin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    var name=req.Name?.Trim();
    if(string.IsNullOrWhiteSpace(name)||name.Length>100||name.Any(char.IsControl))
        return Results.BadRequest("Employee name must be 1–100 characters without control characters.");
    await using var c=await Connect();await using var tx=await c.BeginTransactionAsync();
    string oldName;bool targetGlobal,targetStoreAdmin,inCurrentStore;
    await using(var cmd=new NpgsqlCommand("""
        SELECT e.name,e.global_admin,
          EXISTS(SELECT 1 FROM employee_store_access a WHERE a.employee_id=e.id AND a.role='store_admin'),
          EXISTS(SELECT 1 FROM employee_store_access a WHERE a.employee_id=e.id AND a.store_id=@store)
        FROM employees e WHERE e.id=@id FOR UPDATE
        """,c,tx)){
        cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);
        await using var reader=await cmd.ExecuteReaderAsync();
        if(!await reader.ReadAsync())return Results.NotFound("Employee not found.");
        oldName=reader.GetString(0);targetGlobal=reader.GetBoolean(1);
        targetStoreAdmin=reader.GetBoolean(2);inCurrentStore=reader.GetBoolean(3);
    }
    // Scoped admins may rename themselves or non-admin staff assigned to the current store.
    if(!who.Value.globalAdmin&&id!=who.Value.employee
       &&(!inCurrentStore||targetGlobal||targetStoreAdmin))
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(oldName==name)return Results.Ok(new{updated=false,id,name});
    await using(var update=new NpgsqlCommand("UPDATE employees SET name=@name WHERE id=@id",c,tx)){
        update.Parameters.AddWithValue("name",name);update.Parameters.AddWithValue("id",id);
        await update.ExecuteNonQueryAsync();
    }
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,
        who.Value.globalAdmin?null:who.Value.store,"employee.name_changed",id,
        "Employee display name changed from "+oldName+" to "+name);
    await tx.CommitAsync();return Results.Ok(new{updated=true,id,name});
});

app.MapPut("/api/admin/employees/{id}/id",async (HttpContext ctx,string id,ChangeEmployeeId req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    var newId=req.NewId?.Trim();
    if(newId is null||!Regex.IsMatch(newId,@"^[A-Za-z0-9_-]{2,24}$"))
        return Results.BadRequest("Employee ID must be 2–24 letters, numbers, underscores or hyphens.");
    
    if(id==newId)return Results.BadRequest("New ID must differ.");
    await using var c=await Connect();await using var tx=await c.BeginTransactionAsync();
    await using(var check=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employees WHERE id=@id)",c,tx)){
        check.Parameters.AddWithValue("id",newId);
        if((bool)(await check.ExecuteScalarAsync()??false))return Results.Conflict("Employee ID already exists.");
    }
    // Serialize OWNER identifier changes and persist the new bootstrap identity.
    await using(var ownerSetting=new NpgsqlCommand("SELECT value FROM settings WHERE key='owner_employee_id' FOR UPDATE",c,tx)){
        var ownerId=(string?)await ownerSetting.ExecuteScalarAsync();
        if(ownerId==id){
            await using var setOwner=new NpgsqlCommand("UPDATE settings SET value=@new WHERE key='owner_employee_id'",c,tx);
            setOwner.Parameters.AddWithValue("new",newId);await setOwner.ExecuteNonQueryAsync();
        }
    }
    await using(var change=new NpgsqlCommand("UPDATE employees SET id=@new WHERE id=@old",c,tx)){
        change.Parameters.AddWithValue("new",newId);change.Parameters.AddWithValue("old",id);
        if(await change.ExecuteNonQueryAsync()!=1)return Results.NotFound();
    }
    foreach(var (table,column) in new[]{
        ("employee_store_access","employee_id"),("movements","employee_id"),
        ("movements","note_updated_by"),("movement_note_edits","changed_by"),
        ("items","deleted_by"),("auth_email_tokens","employee_id"),
        ("google_oauth_states","employee_id"),("google_oauth_config","configured_by"),
        ("google_store_accounts","connected_by"),("global_google_sender","connected_by"),
        ("repair_tickets","created_by"),("repair_tickets","assigned_to"),
        ("repair_parts","scanned_by"),("repair_parts","returned_by"),
        ("repair_payments","employee_id"),("repair_events","employee_id"),
        ("repair_tickets","picked_up_by")
    }){
        await using var cmd=new NpgsqlCommand($"UPDATE {table} SET {column}=@new WHERE {column}=@old",c,tx);
        cmd.Parameters.AddWithValue("new",newId);cmd.Parameters.AddWithValue("old",id);
        await cmd.ExecuteNonQueryAsync();
    }
    await using(var revoke=new NpgsqlCommand("DELETE FROM sessions WHERE employee_id=@id",c,tx)){
        revoke.Parameters.AddWithValue("id",id);await revoke.ExecuteNonQueryAsync();
    }
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,null,"employee.id_changed",newId,"Employee ID changed from "+id+" to "+newId);
    await tx.CommitAsync();return Results.Ok(new{updated=true,employeeId=newId});
});

app.MapPost("/api/admin/employees/{id}/stores",async (HttpContext ctx,string id,SelectStore req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.StatusCode(StatusCodes.Status403Forbidden);if(!who.Value.globalAdmin&&who.Value.store!=req.StoreId)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var cmd=new NpgsqlCommand("INSERT INTO employee_store_access(employee_id,store_id) VALUES(@employee,@store) ON CONFLICT DO NOTHING",c,tx);
    cmd.Parameters.AddWithValue("employee",id);cmd.Parameters.AddWithValue("store",req.StoreId??"");
    var inserted=await cmd.ExecuteNonQueryAsync();
    if(inserted>0)await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,req.StoreId,"access.granted",id,"Granted additional store access");
    await tx.CommitAsync();return Results.Ok(new{assigned=true});
});
// Additional store access can be revoked, but an employee must retain their primary location.
app.MapDelete("/api/admin/employees/{id}/stores/{storeId}",async (HttpContext ctx,string id,string storeId)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    if(!await CanManageStore(ctx,storeId))return Results.StatusCode(StatusCodes.Status403Forbidden);
    // Prevent a store administrator from revoking their own access mid-session.
    if(!who.Value.globalAdmin&&id==who.Value.employee)return Results.StatusCode(StatusCodes.Status403Forbidden);

    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    string primaryStore;bool targetGlobalAdmin;
    await using(var getEmployee=new NpgsqlCommand("SELECT primary_store_id,global_admin FROM employees WHERE id=@employee FOR UPDATE",c,tx)){
        getEmployee.Parameters.AddWithValue("employee",id);
        await using var reader=await getEmployee.ExecuteReaderAsync();
        if(!await reader.ReadAsync())return Results.NotFound("Employee not found.");
        primaryStore=reader.GetString(0);targetGlobalAdmin=reader.GetBoolean(1);
    }
    if(primaryStore==storeId)return Results.BadRequest("The employee's primary store access cannot be removed.");
    if(targetGlobalAdmin)return Results.BadRequest("Global administrators already have access to all stores. Change their global administrator role before managing individual store assignments.");

    await using(var remove=new NpgsqlCommand("DELETE FROM employee_store_access WHERE employee_id=@employee AND store_id=@store",c,tx)){
        remove.Parameters.AddWithValue("employee",id);
        remove.Parameters.AddWithValue("store",storeId);
        if(await remove.ExecuteNonQueryAsync()!=1)return Results.NotFound("The employee does not have access to this store.");
    }
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,storeId,"access.revoked",id,"Removed additional store access");
    await tx.CommitAsync();
    return Results.Ok(new{removed=true,employeeId=id,storeId});
});
app.MapGet("/api/admin/employee-access",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();if(!who.Value.admin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT employee_id,store_id,role FROM employee_store_access WHERE @global OR store_id=@store ORDER BY employee_id,store_id",c);
    cmd.Parameters.AddWithValue("global",who.Value.globalAdmin);cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{employeeId=r.GetString(0),storeId=r.GetString(1),role=r.GetString(2)});
    return Results.Ok(list);
});
app.MapPut("/api/admin/employees/{id}/permissions",async (HttpContext ctx,string id,EmployeePermissions req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using(var protect=await Connect()){
        await using var owner=new NpgsqlCommand("SELECT value FROM settings WHERE key='owner_employee_id'",protect);
        if(id==(string?)await owner.ExecuteScalarAsync()&&!req.GlobalAdmin)
            return Results.BadRequest("The recovery administrator cannot be demoted.");
    }
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
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,null,"employee.permissions",id,"Global admin: "+req.GlobalAdmin+"; store admin locations: "+string.Join(", ",req.StoreAdminIds??Array.Empty<string>()));
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


async Task<JsonDocument> TokenRequest(Dictionary<string,string> data){
    using var response=await googleHttp.PostAsync("https://oauth2.googleapis.com/token",new FormUrlEncodedContent(data));
    if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Google token exchange failed: "+(int)response.StatusCode);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
}
async Task SendGmail(string[] recipients,string subject,string body){
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT email,refresh_token FROM global_google_sender WHERE id=1",c);
    string sender,refresh;
    await using(var reader=await cmd.ExecuteReaderAsync()){
        if(!await reader.ReadAsync())throw new InvalidOperationException("Global Google sending account has not been connected");
        sender=reader.GetString(0);refresh=DecryptToken(reader.GetString(1));
    }
    using var tokens=await TokenRequest(new(){
        ["client_id"]=googleCredentials.ClientId,["client_secret"]=googleCredentials.ClientSecret,["refresh_token"]=refresh,["grant_type"]="refresh_token"
    });
    var access=tokens.RootElement.GetProperty("access_token").GetString();
    var safeSubject=Convert.ToBase64String(Encoding.UTF8.GetBytes(subject.Replace("\r"," ").Replace("\n"," ")));
    var mime="From: "+sender+"\r\nTo: "+string.Join(", ",recipients)+"\r\nSubject: =?UTF-8?B?"+safeSubject+"?=\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n"+Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
    var raw=Convert.ToBase64String(Encoding.UTF8.GetBytes(mime)).TrimEnd('=').Replace('+','-').Replace('/','_');
    using var request=new HttpRequestMessage(HttpMethod.Post,"https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
    request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",access);
    request.Content=new StringContent(JsonSerializer.Serialize(new{raw}),Encoding.UTF8,"application/json");
    using var response=await googleHttp.SendAsync(request);
    if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Gmail API send failed: "+(int)response.StatusCode);
}

app.MapGet("/api/admin/google-oauth/config",async (HttpContext ctx)=>{
    if((await Identity(ctx))?.globalAdmin!=true)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT client_id,updated_at FROM google_oauth_config WHERE id=1",c);
    await using var reader=await cmd.ExecuteReaderAsync();
    if(await reader.ReadAsync()){
        var clientId=reader.GetString(0);
        var updatedAt=reader.GetDateTime(1);
        return Results.Ok(new{configured=GoogleReady(),clientId,source="dashboard",updatedAt,encryptionReady=googleKey is {Length:32}});
    }
    return Results.Ok(new{configured=GoogleReady(),clientId=GoogleReady()?googleCredentials.ClientId:null,source=GoogleReady()?"server":"none",updatedAt=(DateTime?)null,encryptionReady=googleKey is {Length:32}});
});

app.MapPost("/api/admin/google-oauth/import",async (HttpContext ctx,GoogleOAuthImportRequest req)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(googleKey is not {Length:32})return Results.BadRequest("The server encryption key is missing. Run the installer to configure GOOGLE_TOKEN_KEY first.");
    if(req.Json is null||req.Json.Length>24_576||req.Json.Length==0)return Results.BadRequest("Choose a Google OAuth JSON file smaller than 24 KB.");
    if(string.IsNullOrEmpty(req.Password))return Results.BadRequest("Confirm your administrator account password.");

    await using var c=await Connect();
    await using(var checkPassword=new NpgsqlCommand("SELECT password_hash FROM employees WHERE id=@id AND active AND global_admin",c)){
        checkPassword.Parameters.AddWithValue("id",who.Value.employee);
        if(!PasswordMatches(req.Password,(string?)await checkPassword.ExecuteScalarAsync()))return Results.Unauthorized();
    }

    string clientId,clientSecret;
    try{
        using var parsed=JsonDocument.Parse(req.Json);
        var root=parsed.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("web",out var web)||web.ValueKind!=JsonValueKind.Object)
            return Results.BadRequest("This must be a Google OAuth client JSON for a Web application.");
        if(!web.TryGetProperty("client_id",out var idField)||idField.ValueKind!=JsonValueKind.String
          ||!web.TryGetProperty("client_secret",out var secretField)||secretField.ValueKind!=JsonValueKind.String)
            return Results.BadRequest("The Google JSON is missing its Client ID or Client Secret.");
        clientId=idField.GetString()??"";
        clientSecret=secretField.GetString()??"";
        if(!Regex.IsMatch(clientId,@"^[A-Za-z0-9_-]+\.apps\.googleusercontent\.com$")
          ||!Regex.IsMatch(clientSecret,@"^[A-Za-z0-9._~-]{10,256}$"))
            return Results.BadRequest("The Google JSON contains invalid OAuth client credentials.");
        if(!web.TryGetProperty("redirect_uris",out var redirects)||redirects.ValueKind!=JsonValueKind.Array
          ||!redirects.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String&&x.GetString()==GoogleRedirect))
            return Results.BadRequest("Add "+GoogleRedirect+" to the Google OAuth client's redirect URIs.");
        if(!web.TryGetProperty("javascript_origins",out var origins)||origins.ValueKind!=JsonValueKind.Array
          ||!origins.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String&&x.GetString()==PublicOrigin))
            return Results.BadRequest("Add "+PublicOrigin+" to the Google OAuth client's authorized JavaScript origins.");
    }catch(JsonException){return Results.BadRequest("The selected file does not contain valid JSON.");}

    // A different OAuth client cannot refresh tokens connected using the previous client.
    if(GoogleReady()&&!string.Equals(googleCredentials.ClientId,clientId,StringComparison.Ordinal)){
        await using var connected=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM global_google_sender WHERE id=1)",c);
        if((bool)(await connected.ExecuteScalarAsync()??false))
            return Results.Conflict("A global Gmail sender is connected. Disconnect the global sender before replacing this configuration with a different OAuth Client ID.");
    }
    await using(var save=new NpgsqlCommand("""
        INSERT INTO google_oauth_config(id,client_id,encrypted_client_secret,configured_by)
        VALUES(1,@id,@secret,@employee)
        ON CONFLICT(id) DO UPDATE SET client_id=excluded.client_id,
            encrypted_client_secret=excluded.encrypted_client_secret,
            configured_by=excluded.configured_by,updated_at=now()
        """,c)){
        save.Parameters.AddWithValue("id",clientId);
        save.Parameters.AddWithValue("secret",EncryptToken(clientSecret));
        save.Parameters.AddWithValue("employee",who.Value.employee);
        await save.ExecuteNonQueryAsync();
    }
    await WriteAdminAudit(c,null,who.Value.employee,null,"oauth.configured",clientId,"Google OAuth client configuration updated");
    // Atomic reference swap activates imported credentials without restarting the API.
    googleCredentials=new GoogleOAuthCredentials(clientId,clientSecret);
    app.Logger.LogInformation("Global administrator {Admin} updated Google OAuth client configuration",who.Value.employee);
    return Results.Ok(new{configured=true,clientId});
}).RequireRateLimiting("sensitive");

// The Gmail sender is a single global account managed only by PIN-authenticated global admins.
app.MapGet("/api/admin/google-sender",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT email,connected_at FROM global_google_sender WHERE id=1",c);
    await using var r=await cmd.ExecuteReaderAsync();
    if(!await r.ReadAsync())return Results.Ok(new{connected=false,email=(string?)null,connectedAt=(DateTime?)null,googleConfigured=GoogleReady(),smtpConfigured=MailConfigured()});
    return Results.Ok(new{connected=true,email=r.GetString(0),connectedAt=(DateTime?)r.GetDateTime(1),googleConfigured=GoogleReady(),smtpConfigured=MailConfigured()});
});
app.MapPost("/api/google/connect",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    if(!GoogleReady())return Results.BadRequest("Google OAuth credentials are not configured on the server.");
    var state=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var verifier=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
    var challenge=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("INSERT INTO google_oauth_states(state_hash,store_id,employee_id,verifier,expires_at) VALUES(@state,@store,@employee,@verifier,now()+interval '10 minutes')",c);
    cmd.Parameters.AddWithValue("state",Hash(state));cmd.Parameters.AddWithValue("store",who.Value.store);cmd.Parameters.AddWithValue("employee",who.Value.employee);cmd.Parameters.AddWithValue("verifier",verifier);
    await cmd.ExecuteNonQueryAsync();
    var query=new Dictionary<string,string>{{"client_id",googleCredentials.ClientId},{"redirect_uri",GoogleRedirect},{"response_type","code"},{"scope","openid email "+GmailScope},{"access_type","offline"},{"prompt","consent"},{"state",state},{"code_challenge",challenge},{"code_challenge_method","S256"}};
    return Results.Ok(new{url="https://accounts.google.com/o/oauth2/v2/auth?"+string.Join("&",query.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value)))});
});
app.MapGet("/api/google/callback",async (string? code,string? state,string? error)=>{
    const string back="/?google=";
    if(error is not null||string.IsNullOrWhiteSpace(code)||string.IsNullOrWhiteSpace(state))return Results.Redirect(back+"cancelled");
    if(!GoogleReady())return Results.Redirect(back+"not_configured");
    try{
        await using var c=await Connect();
        await using var tx=await c.BeginTransactionAsync();
        await using var cmd=new NpgsqlCommand("DELETE FROM google_oauth_states WHERE state_hash=@state AND expires_at>now() RETURNING employee_id,verifier",c,tx);
        cmd.Parameters.AddWithValue("state",Hash(state));
        string employee,verifier;
        await using(var reader=await cmd.ExecuteReaderAsync()){
            if(!await reader.ReadAsync())return Results.Redirect(back+"expired");
            employee=reader.GetString(0);verifier=reader.GetString(1);
        }
        await using var role=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM employees WHERE id=@employee AND active AND global_admin)",c,tx);
        role.Parameters.AddWithValue("employee",employee);
        if(!(bool)(await role.ExecuteScalarAsync()??false))return Results.Redirect(back+"unauthorized");
        using var token=await TokenRequest(new(){
            ["client_id"]=googleCredentials.ClientId,["client_secret"]=googleCredentials.ClientSecret,["code"]=code,
            ["redirect_uri"]=GoogleRedirect,["grant_type"]="authorization_code",["code_verifier"]=verifier
        });
        var refresh=token.RootElement.TryGetProperty("refresh_token",out var rt)?rt.GetString():null;
        if(string.IsNullOrWhiteSpace(refresh))return Results.Redirect(back+"no_refresh_token");
        var access=token.RootElement.GetProperty("access_token").GetString();
        using var profileRequest=new HttpRequestMessage(HttpMethod.Get,"https://openidconnect.googleapis.com/v1/userinfo");
        profileRequest.Headers.Authorization=new AuthenticationHeaderValue("Bearer",access);
        using var profileResponse=await googleHttp.SendAsync(profileRequest);
        if(!profileResponse.IsSuccessStatusCode)throw new InvalidOperationException("Google profile lookup failed");
        using var profile=JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync());
        var email=profile.RootElement.GetProperty("email").GetString()??"";
        if(!ValidEmail(email)||!profile.RootElement.GetProperty("email_verified").GetBoolean())return Results.Redirect(back+"unverified");
        await using var save=new NpgsqlCommand("INSERT INTO global_google_sender(id,email,refresh_token,connected_by) VALUES(1,@email,@token,@employee) ON CONFLICT(id) DO UPDATE SET email=excluded.email,refresh_token=excluded.refresh_token,connected_by=excluded.connected_by,connected_at=now()",c,tx);
        save.Parameters.AddWithValue("email",email);save.Parameters.AddWithValue("token",EncryptToken(refresh));save.Parameters.AddWithValue("employee",employee);
        await save.ExecuteNonQueryAsync();
        await WriteAdminAudit(c,(NpgsqlTransaction)tx,employee,null,"gmail.connected",email,"Global sender authorized");
        await tx.CommitAsync();
        return Results.Redirect(back+"connected");
    }catch(Exception ex){app.Logger.LogError(ex,"Global Google OAuth callback failed");return Results.Redirect(back+"failed");}
});
app.MapPost("/api/google/disconnect",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.globalAdmin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var cmd=new NpgsqlCommand("DELETE FROM global_google_sender WHERE id=1",c,tx);
    if(await cmd.ExecuteNonQueryAsync()>0)await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,null,"gmail.disconnected","Global sender","Google sender disconnected");
    await tx.CommitAsync();
    return Results.Ok(new{disconnected=true});
});

app.MapGet("/api/alerts/config",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!await CanManageStore(ctx,who.Value.store))return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT enabled,recipients FROM store_alert_settings WHERE store_id=@store",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    bool enabled=false;string recipients="";
    if(await r.ReadAsync()){enabled=r.GetBoolean(0);recipients=r.GetString(1);}
    await r.CloseAsync();
    await using var googleCmd=new NpgsqlCommand("SELECT email FROM global_google_sender WHERE id=1",c);
    var globalSenderEmail=(string?)await googleCmd.ExecuteScalarAsync();
    return Results.Ok(new{enabled,recipients,senderConfigured=MailConfigured()||(globalSenderEmail is not null&&GoogleReady()),globalSenderEmail=GoogleReady()?globalSenderEmail:null,senderType=globalSenderEmail is not null&&GoogleReady()?"google":MailConfigured()?"smtp":"none"});
});
app.MapPut("/api/alerts/config",async (HttpContext ctx,AlertSettingsRequest req)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!await CanManageStore(ctx,who.Value.store))return Results.StatusCode(StatusCodes.Status403Forbidden);
    var recipients=ParseRecipients(req.Recipients);
    if(recipients.Length>10||recipients.Any(x=>!ValidEmail(x)))return Results.BadRequest("Enter up to 10 valid email addresses separated by commas.");
    if(req.Enabled&&recipients.Length==0)return Results.BadRequest("Add at least one recipient before enabling alerts.");
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var cmd=new NpgsqlCommand("INSERT INTO store_alert_settings(store_id,enabled,recipients) VALUES(@store,@enabled,@recipients) ON CONFLICT(store_id) DO UPDATE SET enabled=excluded.enabled,recipients=excluded.recipients",c,tx);
    cmd.Parameters.AddWithValue("store",who.Value.store);cmd.Parameters.AddWithValue("enabled",req.Enabled);cmd.Parameters.AddWithValue("recipients",string.Join(",",recipients));
    await cmd.ExecuteNonQueryAsync();
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,who.Value.store,"alerts.updated",who.Value.store,(req.Enabled?"Enabled":"Disabled")+"; "+recipients.Length+" recipient(s)");
    await tx.CommitAsync();return Results.Ok(new{saved=true});
});
app.MapPost("/api/alerts/test",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!await CanManageStore(ctx,who.Value.store))return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT s.name,COALESCE(a.recipients,'') FROM stores s LEFT JOIN store_alert_settings a ON a.store_id=s.id WHERE s.id=@store",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    string name;string[] recipients;
    await using(var r=await cmd.ExecuteReaderAsync()){
        if(!await r.ReadAsync())return Results.NotFound();
        name=r.GetString(0);recipients=ParseRecipients(r.GetString(1));
    }
    if(recipients.Length==0)return Results.BadRequest("Save at least one recipient address for this store first.");
    await using var google=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM global_google_sender WHERE id=1)",c);
    var useGoogle=GoogleReady()&&(bool)(await google.ExecuteScalarAsync()??false);
    if(!useGoogle&&!MailConfigured())return Results.BadRequest("The global administrator must connect a Gmail sender before test emails can be sent.");
    try{
        var subject="ZAGG Inventory Manager test — "+name;
        var body="Test successful. Low-stock notifications for "+name+" will be sent to this address when enabled.";
        if(useGoogle)await SendGmail(recipients,subject,body);
        else await SendMail(recipients,subject,body);
        return Results.Ok(new{sent=true});
    }
    catch(Exception ex){app.Logger.LogError(ex,"Global sender test email failed for store {Store}",who.Value.store);return Results.Problem("Email could not be sent. Check the global Google sender or server email settings.",statusCode:502);}
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
// Authentication email is independent of low-stock recipients: it goes only to the employee.
// An integration-test outbox is enabled ONLY under the explicit IntegrationTest environment.
async Task<bool> AccountMailReady(){
    if(app.Environment.IsEnvironment("IntegrationTest")&&!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTH_EMAIL_TEST_OUTBOX")))return true;
    if(MailConfigured())return true;
    if(!GoogleReady())return false;
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM global_google_sender WHERE id=1)",c);
    return (bool)(await cmd.ExecuteScalarAsync()??false);
}
async Task SendAccountEmail(string email,string subject,string body){
    if(app.Environment.IsEnvironment("IntegrationTest")){
        var outbox=Environment.GetEnvironmentVariable("AUTH_EMAIL_TEST_OUTBOX");
        if(!string.IsNullOrWhiteSpace(outbox)){
            await System.IO.File.AppendAllTextAsync(outbox,JsonSerializer.Serialize(new{email,subject,body})+"\n");
            return;
        }
    }
    if(GoogleReady()){
        await using var c=await Connect();
        await using var cmd=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM global_google_sender WHERE id=1)",c);
        if((bool)(await cmd.ExecuteScalarAsync()??false)){
            await SendGmail(new[]{email},subject,body);
            return;
        }
    }
    if(MailConfigured()){await SendMail(new[]{email},subject,body);return;}
    throw new InvalidOperationException("No company Gmail or SMTP sender has been configured.");
}
async Task<string> CreateAccountEmailToken(NpgsqlConnection c,NpgsqlTransaction tx,string employeeId,string email,string purpose,string? pendingHash,int validMinutes){
    // At most one unexpired activation at a time: a second person cannot silently replace
    // its claimed mailbox. Within 60 seconds, even same-address resends are throttled.
    await using(var existing=new NpgsqlCommand("""
        SELECT email,created_at FROM auth_email_tokens
        WHERE employee_id=@id AND purpose=@purpose AND expires_at>now() FOR UPDATE
        """,c,tx)){
        existing.Parameters.AddWithValue("id",employeeId);existing.Parameters.AddWithValue("purpose",purpose);
        await using var r=await existing.ExecuteReaderAsync();
        if(await r.ReadAsync()){
            if(!string.Equals(r.GetString(0),email,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An activation request for this employee ID is already pending. Contact your administrator if this was not you.");
            if(r.GetDateTime(1)>DateTime.UtcNow.AddSeconds(-60))
                throw new InvalidOperationException("An email was just sent. Please wait a minute before requesting another.");
        }
    }
    var raw=NewAuthCode();
    await using var put=new NpgsqlCommand("""
        INSERT INTO auth_email_tokens(employee_id,purpose,token_hash,email,pending_password_hash,created_at,expires_at)
        VALUES(@id,@purpose,@hash,@email,@pending,now(),now()+(@minutes * interval '1 minute'))
        ON CONFLICT(employee_id,purpose) DO UPDATE SET
        token_hash=excluded.token_hash,email=excluded.email,
        pending_password_hash=excluded.pending_password_hash,created_at=now(),expires_at=excluded.expires_at
        """,c,tx);
    put.Parameters.AddWithValue("id",employeeId);
    put.Parameters.AddWithValue("purpose",purpose);
    put.Parameters.AddWithValue("hash",Hash(raw));
    put.Parameters.AddWithValue("email",email);
    put.Parameters.Add("pending",NpgsqlTypes.NpgsqlDbType.Text).Value=(object?)pendingHash??DBNull.Value;
    put.Parameters.AddWithValue("minutes",validMinutes);
    await put.ExecuteNonQueryAsync();
    return raw;
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

app.MapGet("/api/items/export",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT sku,name,COALESCE(barcode,''),COALESCE(category,''),quantity,min_stock,unit_cost,COALESCE(description,'')
        FROM items WHERE store_id=@store AND deleted_at IS NULL ORDER BY name,sku
        """,c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    var csv=new StringBuilder("SKU,Product,Barcode,Category,Quantity,Minimum Stock,Unit Cost,Description\r\n");
    await using var r=await cmd.ExecuteReaderAsync();
    while(await r.ReadAsync()){
        csv.Append(CsvField(r.GetString(0))).Append(',').Append(CsvField(r.GetString(1))).Append(',')
           .Append(CsvField(r.GetString(2))).Append(',').Append(CsvField(r.GetString(3))).Append(',')
           .Append(r.GetInt32(4)).Append(',').Append(r.GetInt32(5)).Append(',');
        if(!r.IsDBNull(6))csv.Append(r.GetDecimal(6).ToString(System.Globalization.CultureInfo.InvariantCulture));
        csv.Append(',').Append(CsvField(r.GetString(7))).Append("\r\n");
    }
    return CsvFile(csv.ToString(),"zagg-inventory-"+Regex.Replace(who.Value.store,@"[^A-Za-z0-9_-]","_")+".csv");
});
app.MapGet("/api/items/reorder",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT id,sku,name,quantity,min_stock,COALESCE(category,''),COALESCE(barcode,'')
        FROM items WHERE store_id=@store AND deleted_at IS NULL AND quantity<=min_stock
        ORDER BY quantity ASC,name ASC
        """,c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    var list=new List<object>();
    while(await r.ReadAsync()){
        var qty=r.GetInt32(3);var min=r.GetInt32(4);
        list.Add(new{id=r.GetGuid(0),sku=r.GetString(1),name=r.GetString(2),quantity=qty,minStock=min,category=r.GetString(5),barcode=r.GetString(6),reorderQty=Math.Max(1,(long)min+1-qty)});
    }
    return Results.Ok(list);
});
app.MapGet("/api/items/reorder/export",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT sku,name,quantity,min_stock,COALESCE(category,''),COALESCE(barcode,'')
        FROM items WHERE store_id=@store AND deleted_at IS NULL AND quantity<=min_stock
        ORDER BY quantity ASC,name ASC
        """,c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    var csv=new StringBuilder("SKU,Product,Current Stock,Minimum Stock,Order Quantity (to exceed minimum),Category,Barcode\r\n");
    await using var r=await cmd.ExecuteReaderAsync();
    while(await r.ReadAsync()){
        csv.Append(CsvField(r.GetString(0))).Append(',').Append(CsvField(r.GetString(1))).Append(',')
           .Append(r.GetInt32(2)).Append(',').Append(r.GetInt32(3)).Append(',')
           .Append(Math.Max(1,(long)r.GetInt32(3)+1-r.GetInt32(2))).Append(',')
           .Append(CsvField(r.GetString(4))).Append(',').Append(CsvField(r.GetString(5))).Append("\r\n");
    }
    return CsvFile(csv.ToString(),"zagg-reorder-"+Regex.Replace(who.Value.store,@"[^A-Za-z0-9_-]","_")+".csv");
});
app.MapGet("/api/items/{id:guid}/history",async (HttpContext ctx,Guid id)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using(var exists=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM items WHERE id=@id AND store_id=@store AND deleted_at IS NULL)",c)){
        exists.Parameters.AddWithValue("id",id);exists.Parameters.AddWithValue("store",who.Value.store);
        if(!(bool)(await exists.ExecuteScalarAsync()??false))return Results.NotFound();
    }
    await using var cmd=new NpgsqlCommand("""
        SELECT m.id,m.delta,m.before_qty,m.after_qty,m.created_at,COALESCE(m.note,''),m.employee_id,m.note_revision,m.note_updated_at,m.note_updated_by
        FROM movements m WHERE m.item_id=@id AND m.store_id=@store
        ORDER BY m.id DESC LIMIT 200
        """,c);
    cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{id=r.GetInt64(0),delta=r.GetInt32(1),before=r.GetInt32(2),after=r.GetInt32(3),time=r.GetDateTime(4),note=r.GetString(5),employeeId=r.GetString(6),noteRevision=r.GetInt32(7),noteUpdatedAt=r.IsDBNull(8)?(DateTime?)null:r.GetDateTime(8),noteUpdatedBy=r.IsDBNull(9)?null:r.GetString(9)});
    return Results.Ok(list);
});
app.MapGet("/api/admin/audit",async (HttpContext ctx)=>{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(!who.Value.admin)return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("""
        SELECT id,occurred_at,actor_employee_id,store_id,action,target,details
        FROM admin_audit WHERE @global OR store_id=@store
        ORDER BY id DESC LIMIT 200
        """,c);
    cmd.Parameters.AddWithValue("global",who.Value.globalAdmin);cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    var list=new List<object>();
    while(await r.ReadAsync())list.Add(new{id=r.GetInt64(0),time=r.GetDateTime(1),employeeId=r.GetString(2),storeId=r.IsDBNull(3)?null:r.GetString(3),action=r.GetString(4),target=r.GetString(5),details=r.GetString(6)});
    return Results.Ok(list);
});
app.MapGet("/api/items", async (HttpContext ctx) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT id,sku,name,quantity,COALESCE(category,''),COALESCE(barcode,''),min_stock,unit_cost,COALESCE(description,'') FROM items WHERE store_id=@store AND deleted_at IS NULL ORDER BY name LIMIT 1000", c);
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
    await using var select = new NpgsqlCommand("SELECT quantity FROM items WHERE id=@id AND store_id=@store AND deleted_at IS NULL FOR UPDATE",c,tx);
    select.Parameters.AddWithValue("id",id);select.Parameters.AddWithValue("store",who.Value.store);
    var current=await select.ExecuteScalarAsync();
    if (current is null) return Results.NotFound();
    var before=(int)current;
    var after=(long)before+req.Delta;
    if (after<0 || after>int.MaxValue) return Results.BadRequest("Insufficient stock or quantity overflow");
    await using (var update = new NpgsqlCommand("UPDATE items SET quantity=@qty WHERE id=@id AND store_id=@store AND deleted_at IS NULL",c,tx))
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
// Checkout notes can be corrected after the stock movement, without changing quantity, time or employee.
app.MapPut("/api/movements/{id:long}/note",async (HttpContext ctx,long id,CheckoutNoteUpdate req)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    var note=req.Note?.Trim();
    if(string.IsNullOrWhiteSpace(note)||note.Length>1000)
        return Results.BadRequest("Checkout note must contain 1–1000 characters.");
    if(req.ExpectedRevision<0)return Results.BadRequest("Invalid note revision.");
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    string? original;int revision;
    await using(var select=new NpgsqlCommand("""
        SELECT m.note,m.note_revision
        FROM movements m WHERE m.id=@id AND m.store_id=@store AND m.delta<0
        FOR UPDATE
        """,c,tx)){
        select.Parameters.AddWithValue("id",id);
        select.Parameters.AddWithValue("store",who.Value.store);
        await using var reader=await select.ExecuteReaderAsync();
        if(!await reader.ReadAsync())return Results.NotFound();
        original=reader.IsDBNull(0)?null:reader.GetString(0);
        revision=reader.GetInt32(1);
    }
    // Any active employee who can access this store may annotate its checkout history.
    // The revision log records every edit and prevents changes to the stock movement itself.
    if(req.ExpectedRevision!=revision)return Results.Conflict("This note was changed by someone else. Refresh the transaction before editing.");
    if(string.Equals(original?.Trim(),note,StringComparison.Ordinal))
        return Results.Ok(new{updated=false,note,noteRevision=revision});
    DateTime? editedAt=null;
    await using(var update=new NpgsqlCommand("""
        UPDATE movements SET note=@note,note_revision=note_revision+1,
          note_updated_at=now(),note_updated_by=@editor
        WHERE id=@id AND store_id=@store AND delta<0
        RETURNING note_updated_at
        """,c,tx)){
        update.Parameters.AddWithValue("note",note);
        update.Parameters.AddWithValue("editor",who.Value.employee);
        update.Parameters.AddWithValue("id",id);
        update.Parameters.AddWithValue("store",who.Value.store);
        editedAt=(DateTime?)await update.ExecuteScalarAsync();
    }
    await using(var log=new NpgsqlCommand("""
        INSERT INTO movement_note_edits(movement_id,store_id,previous_note,new_note,changed_by,revision)
        VALUES(@id,@store,@previous,@note,@editor,@revision)
        """,c,tx)){
        log.Parameters.AddWithValue("id",id);
        log.Parameters.AddWithValue("store",who.Value.store);
        log.Parameters.Add("previous",NpgsqlTypes.NpgsqlDbType.Text).Value=(object?)original??DBNull.Value;
        log.Parameters.AddWithValue("note",note);
        log.Parameters.AddWithValue("editor",who.Value.employee);
        log.Parameters.AddWithValue("revision",revision+1);
        await log.ExecuteNonQueryAsync();
    }
    if(who.Value.admin)
        await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,who.Value.store,
            "movement.note_updated",id.ToString(),"Checkout note revised");
    await tx.CommitAsync();
    return Results.Ok(new{updated=true,note,noteRevision=revision+1,noteUpdatedBy=who.Value.employee,noteUpdatedAt=editedAt});
});
app.MapGet("/api/movements/{id:long}/note-edits",async (HttpContext ctx,long id)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using(var exists=new NpgsqlCommand(
        "SELECT EXISTS(SELECT 1 FROM movements WHERE id=@id AND store_id=@store AND delta<0)",c)){
        exists.Parameters.AddWithValue("id",id);
        exists.Parameters.AddWithValue("store",who.Value.store);
        if(!(bool)(await exists.ExecuteScalarAsync()??false))return Results.NotFound();
    }
    await using var cmd=new NpgsqlCommand("""
        SELECT revision,previous_note,new_note,changed_by,changed_at
        FROM movement_note_edits
        WHERE movement_id=@id AND store_id=@store
        ORDER BY id DESC LIMIT 100
        """,c);
    cmd.Parameters.AddWithValue("id",id);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var reader=await cmd.ExecuteReaderAsync();
    var history=new List<object>();
    while(await reader.ReadAsync())history.Add(new{
        revision=reader.GetInt32(0),
        previousNote=reader.IsDBNull(1)?null:reader.GetString(1),
        note=reader.GetString(2),
        employeeId=reader.GetString(3),
        time=reader.GetDateTime(4)
    });
    return Results.Ok(history);
});
app.MapGet("/api/movements", async (HttpContext ctx) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c = await Connect();
    await using var cmd = new NpgsqlCommand("SELECT m.id,i.sku,i.name,m.delta,m.before_qty,m.after_qty,m.created_at,m.note,m.store_id,m.employee_id,m.note_revision,m.note_updated_at,m.note_updated_by FROM movements m JOIN items i ON i.id=m.item_id WHERE m.store_id=@store ORDER BY m.id DESC LIMIT 200",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    var list=new List<object>();
    while(await r.ReadAsync()) list.Add(new{id=r.GetInt64(0),sku=r.GetString(1),name=r.GetString(2),delta=r.GetInt32(3),before=r.GetInt32(4),after=r.GetInt32(5),time=r.GetDateTime(6),note=r.IsDBNull(7)?null:r.GetString(7),storeId=r.GetString(8),employeeId=r.GetString(9),noteRevision=r.GetInt32(10),noteUpdatedAt=r.IsDBNull(11)?(DateTime?)null:r.GetDateTime(11),noteUpdatedBy=r.IsDBNull(12)?null:r.GetString(12)});
    return Results.Ok(list);
});

app.MapGet("/api/summary", async (HttpContext ctx) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT count(*),COALESCE(sum(quantity),0),count(*) FILTER(WHERE quantity<=min_stock),COALESCE(sum(quantity*unit_cost),0) FROM items WHERE store_id=@store AND deleted_at IS NULL",c);
    cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();await r.ReadAsync();
    return Results.Ok(new{products=r.GetInt64(0),units=r.GetInt64(1),lowStock=r.GetInt64(2),value=r.GetDecimal(3)});
});
app.MapGet("/api/items/lookup/{code}", async (HttpContext ctx, string code) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("SELECT id,sku,name,quantity FROM items WHERE store_id=@store AND deleted_at IS NULL AND (sku=@code OR barcode=@code) LIMIT 1",c);
    cmd.Parameters.AddWithValue("code",code);cmd.Parameters.AddWithValue("store",who.Value.store);
    await using var r=await cmd.ExecuteReaderAsync();
    return await r.ReadAsync()?Results.Ok(new{id=r.GetGuid(0),sku=r.GetString(1),name=r.GetString(2),quantity=r.GetInt32(3)}):Results.NotFound();
});
app.MapPut("/api/items/{id:guid}", async (HttpContext ctx, Guid id, EditItem req) =>
{
    var who=await Identity(ctx);if(who is null)return Results.Unauthorized();
    if(string.IsNullOrWhiteSpace(req.Name)||string.IsNullOrWhiteSpace(req.Sku)||req.MinStock<0||req.UnitCost<0)return Results.BadRequest("Invalid item details");
    await using var c=await Connect();
    await using var cmd=new NpgsqlCommand("UPDATE items SET sku=@sku,name=@name,barcode=NULLIF(@barcode,''),category=@category,description=@description,min_stock=@min,unit_cost=@cost,updated_at=now() WHERE id=@id AND store_id=@store AND deleted_at IS NULL",c);
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

app.MapPost("/api/items/{id:guid}/delete",async (HttpContext ctx,Guid id,DeleteItemRequest req)=>{
    var who=await Identity(ctx);
    if(who is null)return Results.Unauthorized();
    // Product removal is restricted to a store administrator or global administrator.
    if(!await CanManageStore(ctx,who.Value.store))return Results.StatusCode(StatusCodes.Status403Forbidden);
    await using var c=await Connect();
    await using var tx=await c.BeginTransactionAsync();
    await using var find=new NpgsqlCommand("SELECT name FROM items WHERE id=@id AND store_id=@store AND deleted_at IS NULL FOR UPDATE",c,tx);
    find.Parameters.AddWithValue("id",id);find.Parameters.AddWithValue("store",who.Value.store);
    var name=(string?)await find.ExecuteScalarAsync();
    if(name is null)return Results.NotFound();
    var required=name.ToUpperInvariant();
    if(!string.Equals(req.Confirmation,required,StringComparison.Ordinal))
        return Results.BadRequest("Confirmation must match the displayed uppercase product name exactly, including case and spaces.");
    await using var update=new NpgsqlCommand("UPDATE items SET deleted_at=now(),deleted_by=@employee,updated_at=now() WHERE id=@id AND store_id=@store AND deleted_at IS NULL",c,tx);
    update.Parameters.AddWithValue("id",id);update.Parameters.AddWithValue("store",who.Value.store);update.Parameters.AddWithValue("employee",who.Value.employee);
    await update.ExecuteNonQueryAsync();
    await using var pending=new NpgsqlCommand("DELETE FROM alert_outbox WHERE item_id=@id AND sent_at IS NULL",c,tx);
    pending.Parameters.AddWithValue("id",id);await pending.ExecuteNonQueryAsync();
    await WriteAdminAudit(c,(NpgsqlTransaction)tx,who.Value.employee,who.Value.store,"product.deleted",id.ToString(),name);
    await tx.CommitAsync();
    app.Logger.LogInformation("Product {ProductId} archived by employee {Employee} in store {Store}",id,who.Value.employee,who.Value.store);
    return Results.Ok(new{deleted=true});
});
// Repair endpoints share the existing session identity, store permissions and low-stock outbox.
RepairQueueEndpoints.Map(app,Connect,Identity,QueueLowStock);

// Delivery loop runs on the API host; inventory operations never depend on Gmail availability.
await using (var c=await Connect())
{
    await using var cmd=new NpgsqlCommand("""
    CREATE TABLE IF NOT EXISTS settings(key text PRIMARY KEY,value text NOT NULL);
    CREATE TABLE IF NOT EXISTS auth_login_failures(attempt_key text PRIMARY KEY,failures integer NOT NULL,last_failure timestamptz NOT NULL,blocked_until timestamptz);
    CREATE TABLE IF NOT EXISTS admin_audit(
        id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        occurred_at timestamptz NOT NULL DEFAULT now(),
        actor_employee_id text NOT NULL,
        store_id text,
        action text NOT NULL,
        target text NOT NULL,
        details text NOT NULL DEFAULT ''
    );
    CREATE INDEX IF NOT EXISTS admin_audit_by_store_time ON admin_audit(store_id,occurred_at DESC,id DESC);

    CREATE TABLE IF NOT EXISTS stores(id text PRIMARY KEY,name text NOT NULL,active boolean NOT NULL DEFAULT true);
    CREATE TABLE IF NOT EXISTS store_alert_settings(store_id text PRIMARY KEY REFERENCES stores(id) ON UPDATE CASCADE,enabled boolean NOT NULL DEFAULT false,recipients text NOT NULL DEFAULT '');
    CREATE TABLE IF NOT EXISTS google_store_accounts(store_id text PRIMARY KEY REFERENCES stores(id) ON UPDATE CASCADE,email text NOT NULL,refresh_token text NOT NULL,connected_by text NOT NULL,connected_at timestamptz NOT NULL DEFAULT now());
    CREATE TABLE IF NOT EXISTS global_google_sender(id smallint PRIMARY KEY CHECK(id=1),email text NOT NULL,refresh_token text NOT NULL,connected_by text NOT NULL,connected_at timestamptz NOT NULL DEFAULT now());
    CREATE TABLE IF NOT EXISTS google_oauth_config(
        id smallint PRIMARY KEY CHECK(id=1),
        client_id text NOT NULL,
        encrypted_client_secret text NOT NULL,
        configured_by text NOT NULL,
        updated_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE TABLE IF NOT EXISTS google_oauth_states(state_hash text PRIMARY KEY,store_id text NOT NULL REFERENCES stores(id) ON UPDATE CASCADE,employee_id text NOT NULL,verifier text NOT NULL,expires_at timestamptz NOT NULL);
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
    INSERT INTO settings(key,value) VALUES ('owner_employee_id','OWNER') ON CONFLICT (key) DO NOTHING;
    INSERT INTO employees(store_id,id,name,primary_store_id)
      SELECT id,(SELECT value FROM settings WHERE key='owner_employee_id'),'Original Administrator',id
      FROM stores
      WHERE NOT EXISTS(SELECT 1 FROM employees WHERE id=(SELECT value FROM settings WHERE key='owner_employee_id'))
      ORDER BY id LIMIT 1 ON CONFLICT DO NOTHING;
    INSERT INTO employee_store_access(employee_id,store_id) SELECT id,store_id FROM employees ON CONFLICT DO NOTHING;
    UPDATE employees SET global_admin=true WHERE id=(SELECT value FROM settings WHERE key='owner_employee_id');
    CREATE TABLE IF NOT EXISTS sessions(token_hash text PRIMARY KEY,expires_at timestamptz NOT NULL);
    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS store_id text NOT NULL DEFAULT 'DEFAULT';
    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS employee_id text NOT NULL DEFAULT 'OWNER';
    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS is_admin boolean NOT NULL DEFAULT false;
    CREATE TABLE IF NOT EXISTS items(id uuid PRIMARY KEY,sku text NOT NULL UNIQUE,name text NOT NULL,quantity integer NOT NULL DEFAULT 0 CHECK(quantity>=0));
    CREATE TABLE IF NOT EXISTS movements(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,item_id uuid NOT NULL REFERENCES items(id),delta integer NOT NULL CHECK(delta<>0),before_qty integer NOT NULL,after_qty integer NOT NULL,note text,created_at timestamptz NOT NULL DEFAULT now());
    ALTER TABLE items ADD COLUMN IF NOT EXISTS store_id text NOT NULL DEFAULT 'DEFAULT' REFERENCES stores(id);
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS store_id text NOT NULL DEFAULT 'DEFAULT' REFERENCES stores(id);
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS employee_id text NOT NULL DEFAULT 'LEGACY';
    ALTER TABLE items ADD COLUMN IF NOT EXISTS deleted_at timestamptz;
    ALTER TABLE items DROP CONSTRAINT IF EXISTS items_sku_key;
    DROP INDEX IF EXISTS items_store_sku_idx;
    CREATE UNIQUE INDEX IF NOT EXISTS items_store_sku_idx ON items(store_id,sku) WHERE deleted_at IS NULL;
    ALTER TABLE items ADD COLUMN IF NOT EXISTS category text NOT NULL DEFAULT '';
    ALTER TABLE items ADD COLUMN IF NOT EXISTS description text NOT NULL DEFAULT '';
    ALTER TABLE items ADD COLUMN IF NOT EXISTS barcode text;
    ALTER TABLE items ADD COLUMN IF NOT EXISTS min_stock integer NOT NULL DEFAULT 0;
    ALTER TABLE items ADD COLUMN IF NOT EXISTS unit_cost numeric(12,2);
    ALTER TABLE items ADD COLUMN IF NOT EXISTS updated_at timestamptz NOT NULL DEFAULT now();
    ALTER TABLE items ADD COLUMN IF NOT EXISTS deleted_by text;
    DROP INDEX IF EXISTS items_barcode_idx;
    DROP INDEX IF EXISTS items_store_barcode_idx;
    CREATE UNIQUE INDEX IF NOT EXISTS items_store_barcode_idx ON items(store_id,barcode) WHERE barcode IS NOT NULL AND deleted_at IS NULL;
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS note_revision integer NOT NULL DEFAULT 0;
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS note_updated_at timestamptz;
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS note_updated_by text;
    CREATE TABLE IF NOT EXISTS movement_note_edits(
        id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
        movement_id bigint NOT NULL REFERENCES movements(id),
        store_id text NOT NULL,
        previous_note text,
        new_note text NOT NULL,
        changed_by text NOT NULL,
        changed_at timestamptz NOT NULL DEFAULT now(),
        revision integer NOT NULL
    );
    CREATE INDEX IF NOT EXISTS movement_note_edits_history_idx ON movement_note_edits(store_id,movement_id,id DESC);
    ALTER TABLE movements ADD COLUMN IF NOT EXISTS idempotency_key uuid;
    CREATE UNIQUE INDEX IF NOT EXISTS movements_idempotency_key_idx ON movements(idempotency_key) WHERE idempotency_key IS NOT NULL;
    CREATE INDEX IF NOT EXISTS movements_store_item_recent_idx ON movements(store_id,item_id,id DESC);

    -- Repairs: append-only usage history and atomic inventory adjustments per store.
    CREATE TABLE IF NOT EXISTS repair_tickets(
      id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
      store_id text NOT NULL REFERENCES stores(id),
      created_by text NOT NULL,
      assigned_to text,
      request_key uuid,
      device text NOT NULL,
      repair_type text NOT NULL,
      customer_name text,
      customer_contact text,
      serial_number text,
      notes text,
      quoted_total numeric(12,2) NOT NULL CHECK(quoted_total>=0),
      paid_upfront numeric(12,2) NOT NULL CHECK(paid_upfront>=0 AND paid_upfront<=quoted_total),
      status text NOT NULL DEFAULT 'checked_in' CHECK(status IN ('checked_in','in_progress','waiting_parts','ready','completed','cancelled')),
      created_at timestamptz NOT NULL DEFAULT now(),
      updated_at timestamptz NOT NULL DEFAULT now(),
      closed_at timestamptz
    );
    ALTER TABLE repair_tickets ADD COLUMN IF NOT EXISTS request_key uuid;
    CREATE UNIQUE INDEX IF NOT EXISTS repair_tickets_request_key_idx ON repair_tickets(request_key) WHERE request_key IS NOT NULL;
    -- Existing tickets adopt their check-in employee as technician too.
    -- Keep attribution consistent even for tickets created before this change.
    ALTER TABLE repair_tickets DROP CONSTRAINT IF EXISTS repair_tickets_creator_assigned_check;
    UPDATE repair_tickets SET assigned_to=created_by WHERE assigned_to IS DISTINCT FROM created_by;
    ALTER TABLE repair_tickets ADD COLUMN IF NOT EXISTS picked_up_at timestamptz;
    ALTER TABLE repair_tickets ADD COLUMN IF NOT EXISTS picked_up_by text;
    ALTER TABLE repair_tickets ADD COLUMN IF NOT EXISTS pickup_request_key uuid;
    CREATE UNIQUE INDEX IF NOT EXISTS repair_tickets_pickup_key_idx
      ON repair_tickets(pickup_request_key) WHERE pickup_request_key IS NOT NULL;
    CREATE INDEX IF NOT EXISTS repair_tickets_store_queue_idx
      ON repair_tickets(store_id,created_at,id);
    CREATE TABLE IF NOT EXISTS repair_parts(
      id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
      repair_id bigint NOT NULL REFERENCES repair_tickets(id),
      store_id text NOT NULL REFERENCES stores(id),
      item_id uuid NOT NULL REFERENCES items(id),
      quantity integer NOT NULL CHECK(quantity>0),
      scanned_by text NOT NULL,
      used_at timestamptz NOT NULL DEFAULT now(),
      movement_id bigint NOT NULL UNIQUE REFERENCES movements(id),
      request_key uuid NOT NULL UNIQUE,
      returned_at timestamptz,
      returned_by text,
      return_key uuid UNIQUE,
      return_movement_id bigint UNIQUE REFERENCES movements(id)
    );
    CREATE INDEX IF NOT EXISTS repair_parts_by_repair_idx ON repair_parts(repair_id,id);
    CREATE TABLE IF NOT EXISTS repair_payments(
      id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
      repair_id bigint NOT NULL REFERENCES repair_tickets(id),
      store_id text NOT NULL REFERENCES stores(id),
      employee_id text NOT NULL,
      amount numeric(12,2) NOT NULL CHECK(amount>0),
      request_key uuid NOT NULL UNIQUE,
      created_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX IF NOT EXISTS repair_payments_by_repair_idx ON repair_payments(repair_id,id);
    CREATE TABLE IF NOT EXISTS repair_events(
      id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
      repair_id bigint NOT NULL REFERENCES repair_tickets(id),
      store_id text NOT NULL REFERENCES stores(id),
      employee_id text NOT NULL,
      kind text NOT NULL,
      details text NOT NULL DEFAULT '',
      created_at timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX IF NOT EXISTS repair_events_by_repair_idx ON repair_events(repair_id,id DESC);

    -- v1.1.0: employee email/password identity and one-time verification/reset links.
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS theme text NOT NULL DEFAULT 'mint';
    INSERT INTO settings(key,value) VALUES ('login_theme','mint') ON CONFLICT(key) DO NOTHING;
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS email text;
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS email_verified_at timestamptz;
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS password_hash text;
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS password_updated_at timestamptz;
    ALTER TABLE employees ADD COLUMN IF NOT EXISTS bootstrap_provisional boolean NOT NULL DEFAULT false;
    CREATE UNIQUE INDEX IF NOT EXISTS employees_email_unique_idx ON employees(lower(email)) WHERE email IS NOT NULL;
    CREATE TABLE IF NOT EXISTS auth_email_tokens(
        employee_id text NOT NULL,
        purpose text NOT NULL CHECK (purpose IN ('activate','reset','verify')),
        token_hash text NOT NULL UNIQUE,
        email text NOT NULL,
        pending_password_hash text,
        created_at timestamptz NOT NULL DEFAULT now(),
        expires_at timestamptz NOT NULL,
        PRIMARY KEY(employee_id,purpose)
    );
    ALTER TABLE auth_email_tokens DROP CONSTRAINT IF EXISTS auth_email_tokens_purpose_check;
    ALTER TABLE auth_email_tokens ADD CONSTRAINT auth_email_tokens_purpose_check CHECK (purpose IN ('activate','reset','verify','change_email'));
    CREATE INDEX IF NOT EXISTS auth_email_tokens_expiry_idx ON auth_email_tokens(expires_at);
    ALTER TABLE sessions ADD COLUMN IF NOT EXISTS last_seen_at timestamptz NOT NULL DEFAULT now();
    -- Every legacy PIN/ID-only bearer session is invalidated exactly once during migration.
    DO $auth_upgrade$
    BEGIN
      IF NOT EXISTS(SELECT 1 FROM settings WHERE key='password_auth_v2') THEN
        DELETE FROM sessions;
        INSERT INTO settings(key,value) VALUES ('password_auth_v2','enabled');
      END IF;
    END $auth_upgrade$;
    """,c);
    await cmd.ExecuteNonQueryAsync();
}
await using(var configConnection=await Connect()){
    await using var loadConfig=new NpgsqlCommand("SELECT client_id,encrypted_client_secret FROM google_oauth_config WHERE id=1",configConnection);
    await using var stored=await loadConfig.ExecuteReaderAsync();
    if(await stored.ReadAsync()){
        try{
            var clientId=stored.GetString(0);
            var clientSecret=DecryptToken(stored.GetString(1));
            googleCredentials=new GoogleOAuthCredentials(clientId,clientSecret);
        }catch(Exception ex){
            app.Logger.LogError(ex,"Cannot decrypt the saved Google OAuth configuration; check the persistent GOOGLE_TOKEN_KEY");
            googleCredentials=new GoogleOAuthCredentials("","");
        }
    }
}
var mailCts=new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(()=>mailCts.Cancel());
_ = Task.Run(async ()=>{
    while(!mailCts.IsCancellationRequested){
        try{
            if(MailConfigured()||GoogleReady()){
                await using var c=await Connect();
                await using var cmd=new NpgsqlCommand("""
                SELECT o.id,o.store_id,o.subject,o.body,a.recipients,(@googleReady AND EXISTS(SELECT 1 FROM global_google_sender WHERE id=1))
                FROM alert_outbox o JOIN store_alert_settings a ON a.store_id=o.store_id
                WHERE o.sent_at IS NULL AND o.next_attempt<=now() AND a.enabled AND a.recipients<>'' AND ((@googleReady AND EXISTS(SELECT 1 FROM global_google_sender WHERE id=1)) OR @smtp)
                ORDER BY o.id LIMIT 20
                """,c);
                cmd.Parameters.AddWithValue("smtp",MailConfigured());
                cmd.Parameters.AddWithValue("googleReady",GoogleReady());
                var pending=new List<(long id,string store,string subject,string body,string recipients,bool google)>();
                await using(var r=await cmd.ExecuteReaderAsync()){while(await r.ReadAsync())pending.Add((r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetBoolean(5)));}
                foreach(var job in pending){
                    try{
                        if(job.google)await SendGmail(ParseRecipients(job.recipients),job.subject,job.body);else await SendMail(ParseRecipients(job.recipients),job.subject,job.body);
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

app.Run();
record Setup(string? SetupToken,string? Email,string? ConfirmEmail,string? Password,string? ConfirmPassword);
record ActivateAccount(string? EmployeeId,string? Email,string? ConfirmEmail,string? Password,string? ConfirmPassword);
record ForgotPassword(string? EmployeeId);
record ChangeEmail(string? Email,string? ConfirmEmail,string? Password);
record ChangeEmployeeId(string? NewId);
record AuthEmailToken(string? Token);
record CompleteAuthToken(string? Token,string? Password,string? ConfirmPassword);
record NewItem(string Sku,string Name,int Quantity,string? Barcode="",string? Category="",string? Description="",int MinStock=0,decimal? UnitCost=null);
record MovementRequest(int Delta,string? Note,Guid IdempotencyKey);
record CheckoutNoteUpdate(string? Note,int ExpectedRevision);
record EditItem(string Sku,string Name,string? Barcode,string? Category,string? Description,int MinStock,decimal? UnitCost);

record EmployeeLogin(string? EmployeeId,string? Password);
record SelectStore(string? StoreId);
record StoreRequest(string? Id,string? Name);
record EmployeeRequest(string? StoreId,string? Id,string? Name);
record EmployeeNameRequest(string? Name);
record ThemeChoice(string? Theme);

record StoreNameRequest(string? Name);
record AlertSettingsRequest(bool Enabled,string? Recipients);

record EmployeePermissions(bool GlobalAdmin,string[]? StoreAdminIds);

record DeleteItemRequest(string? Confirmation);

record GoogleOAuthCredentials(string ClientId,string ClientSecret);
record GoogleOAuthImportRequest(string? Json,string? Password);
