using Npgsql;
using NpgsqlTypes;

// Repair tickets are deliberately store-scoped. Stock and repair-part rows are
// committed in the same PostgreSQL transaction, never via separate API calls.
static class RepairQueueEndpoints
{
    const decimal MaxMoney = 99999999.99m;
    static bool ValidMoney(decimal amount) => amount >= 0 && amount <= MaxMoney && decimal.Round(amount,2) == amount;
    static bool ValidText(string? value,int max) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= max;
    static bool ValidOptional(string? value,int max) => value is null || value.Trim().Length <= max;
    static bool IsClosed(string status) => status is "completed" or "cancelled";
    static bool ValidStatus(string? status) => status is "checked_in" or "in_progress" or "waiting_parts" or "ready" or "completed" or "cancelled";
    static async Task Event(NpgsqlConnection c,NpgsqlTransaction tx,long ticket,string store,string employee,string kind,string details)
    {
        await using var cmd=new NpgsqlCommand("""
            INSERT INTO repair_events(repair_id,store_id,employee_id,kind,details)
            VALUES(@repair,@store,@employee,@kind,@details)
            """,c,tx);
        cmd.Parameters.AddWithValue("repair",ticket);
        cmd.Parameters.AddWithValue("store",store);
        cmd.Parameters.AddWithValue("employee",employee);
        cmd.Parameters.AddWithValue("kind",kind);
        cmd.Parameters.AddWithValue("details",details);
        await cmd.ExecuteNonQueryAsync();
    }
    static async Task LockKey(NpgsqlConnection c,NpgsqlTransaction tx,Guid key)
    {
        // Serializes concurrent retries before the idempotency lookup. Locks are
        // transaction-scoped and released automatically on commit/rollback.
        await using var cmd=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))",c,tx);
        cmd.Parameters.AddWithValue("key",key.ToString("N"));
        await cmd.ExecuteNonQueryAsync();
    }
    static async Task<bool> AvailableEmployee(NpgsqlConnection c,NpgsqlTransaction tx,string store,string employee)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM employees e
              JOIN employee_store_access access ON access.employee_id=e.id AND access.store_id=@store
              WHERE e.id=@employee AND e.active)
            """,c,tx);
        cmd.Parameters.AddWithValue("store",store);
        cmd.Parameters.AddWithValue("employee",employee);
        return (bool)(await cmd.ExecuteScalarAsync()??false);
    }
    public static void Map(
        WebApplication app,
        Func<Task<NpgsqlConnection>> connect,
        Func<HttpContext,Task<(string store,string employee,bool admin,bool globalAdmin)?>> identity,
        Func<NpgsqlConnection,NpgsqlTransaction,Guid,string,Task> queueLowStock)
    {
        app.MapGet("/api/repairs",async (HttpContext ctx,bool history=false)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            await using var c=await connect();
            await using var cmd=new NpgsqlCommand("""
                SELECT r.id,r.device,r.repair_type,r.created_by,COALESCE(creator.name,r.created_by),
                       r.assigned_to,r.status,r.quoted_total,r.paid_upfront,
                       COALESCE((SELECT sum(amount) FROM repair_payments WHERE repair_id=r.id),0),
                       r.created_at,r.updated_at,r.closed_at,
                       (SELECT COALESCE(sum(quantity),0) FROM repair_parts WHERE repair_id=r.id AND returned_at IS NULL),
                       COALESCE(r.customer_name,''),COALESCE(r.serial_number,'')
                FROM repair_tickets r LEFT JOIN employees creator ON creator.id=r.created_by
                WHERE r.store_id=@store
                  AND (CASE WHEN @history THEN r.status IN ('completed','cancelled')
                            ELSE r.status NOT IN ('completed','cancelled') END)
                ORDER BY CASE WHEN @history THEN COALESCE(r.closed_at,r.updated_at) END DESC NULLS LAST,
                         CASE WHEN NOT @history THEN r.created_at END ASC,r.id ASC
                LIMIT 500
                """,c);
            cmd.Parameters.AddWithValue("store",who.Value.store);
            cmd.Parameters.AddWithValue("history",history);
            await using var reader=await cmd.ExecuteReaderAsync();
            var rows=new List<object>();
            while(await reader.ReadAsync()){
                var quoted=reader.GetDecimal(7);var upfront=reader.GetDecimal(8);var extra=reader.GetDecimal(9);
                rows.Add(new{
                    id=reader.GetInt64(0),device=reader.GetString(1),repairType=reader.GetString(2),
                    createdBy=reader.GetString(3),createdByName=reader.GetString(4),
                    assignedTo=reader.IsDBNull(5)?null:reader.GetString(5),status=reader.GetString(6),
                    quotedTotal=quoted,paidUpfront=upfront,additionalPaid=extra,
                    balanceDue=quoted-upfront-extra,createdAt=reader.GetDateTime(10),
                    updatedAt=reader.GetDateTime(11),closedAt=reader.IsDBNull(12)?(DateTime?)null:reader.GetDateTime(12),
                    partsUsed=reader.GetInt64(13),customerName=reader.GetString(14),serialNumber=reader.GetString(15)
                });
            }
            return Results.Ok(rows);
        });
        app.MapGet("/api/repairs/technicians",async (HttpContext ctx)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            await using var c=await connect();
            await using var cmd=new NpgsqlCommand("""
                SELECT e.id,e.name FROM employees e JOIN employee_store_access access
                ON access.employee_id=e.id AND access.store_id=@store
                WHERE e.active ORDER BY e.name,e.id
                """,c);
            cmd.Parameters.AddWithValue("store",who.Value.store);
            await using var reader=await cmd.ExecuteReaderAsync();
            var rows=new List<object>();
            while(await reader.ReadAsync())rows.Add(new{id=reader.GetString(0),name=reader.GetString(1)});
            return Results.Ok(rows);
        });
        app.MapGet("/api/repairs/{id:long}",async (HttpContext ctx,long id)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            await using var c=await connect();
            object? ticket=null;
            await using(var cmd=new NpgsqlCommand("""
                SELECT r.id,r.device,r.repair_type,r.created_by,COALESCE(e.name,r.created_by),
                  r.assigned_to,r.status,r.quoted_total,r.paid_upfront,
                  COALESCE((SELECT sum(p.amount) FROM repair_payments p WHERE p.repair_id=r.id),0),
                  r.created_at,r.closed_at,r.updated_at,
                  COALESCE(r.customer_name,''),COALESCE(r.customer_contact,''),COALESCE(r.serial_number,''),COALESCE(r.notes,'')
                FROM repair_tickets r LEFT JOIN employees e ON e.id=r.created_by
                WHERE r.id=@id AND r.store_id=@store
                """,c)){
                cmd.Parameters.AddWithValue("id",id);
                cmd.Parameters.AddWithValue("store",who.Value.store);
                await using var reader=await cmd.ExecuteReaderAsync();
                if(await reader.ReadAsync()){
                    var quote=reader.GetDecimal(7);var upfront=reader.GetDecimal(8);var extra=reader.GetDecimal(9);
                    ticket=new{
                        id=reader.GetInt64(0),device=reader.GetString(1),repairType=reader.GetString(2),
                        createdBy=reader.GetString(3),createdByName=reader.GetString(4),
                        assignedTo=reader.IsDBNull(5)?null:reader.GetString(5),status=reader.GetString(6),
                        quotedTotal=quote,paidUpfront=upfront,additionalPaid=extra,balanceDue=quote-upfront-extra,
                        createdAt=reader.GetDateTime(10),
                        closedAt=reader.IsDBNull(11)?(DateTime?)null:reader.GetDateTime(11),
                        updatedAt=reader.GetDateTime(12),customerName=reader.GetString(13),
                        customerContact=reader.GetString(14),serialNumber=reader.GetString(15),
                        notes=reader.GetString(16)
                    };
                }
            }
            if(ticket is null)return Results.NotFound();
            var parts=new List<object>();
            await using(var cmd=new NpgsqlCommand("""
                SELECT p.id,p.item_id,i.name,i.sku,p.quantity,p.scanned_by,p.used_at,p.movement_id,
                       p.returned_at,p.returned_by,p.return_movement_id
                FROM repair_parts p JOIN items i ON i.id=p.item_id
                WHERE p.repair_id=@id AND p.store_id=@store ORDER BY p.id
                """,c)){
                cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);
                await using var reader=await cmd.ExecuteReaderAsync();
                while(await reader.ReadAsync())parts.Add(new{
                    id=reader.GetInt64(0),itemId=reader.GetGuid(1),name=reader.GetString(2),
                    sku=reader.GetString(3),quantity=reader.GetInt32(4),scannedBy=reader.GetString(5),
                    usedAt=reader.GetDateTime(6),movementId=reader.GetInt64(7),
                    returnedAt=reader.IsDBNull(8)?(DateTime?)null:reader.GetDateTime(8),
                    returnedBy=reader.IsDBNull(9)?null:reader.GetString(9),
                    returnMovementId=reader.IsDBNull(10)?(long?)null:reader.GetInt64(10)
                });
            }
            var payments=new List<object>();
            await using(var cmd=new NpgsqlCommand("""
                SELECT id,amount,employee_id,created_at FROM repair_payments
                WHERE repair_id=@id AND store_id=@store ORDER BY id
                """,c)){
                cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);
                await using var reader=await cmd.ExecuteReaderAsync();
                while(await reader.ReadAsync())payments.Add(new{
                    id=reader.GetInt64(0),amount=reader.GetDecimal(1),employeeId=reader.GetString(2),
                    createdAt=reader.GetDateTime(3)
                });
            }
            var events=new List<object>();
            await using(var cmd=new NpgsqlCommand("""
                SELECT kind,details,employee_id,created_at FROM repair_events
                WHERE repair_id=@id AND store_id=@store ORDER BY id DESC LIMIT 100
                """,c)){
                cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);
                await using var reader=await cmd.ExecuteReaderAsync();
                while(await reader.ReadAsync())events.Add(new{
                    kind=reader.GetString(0),details=reader.GetString(1),
                    employeeId=reader.GetString(2),createdAt=reader.GetDateTime(3)
                });
            }
            return Results.Ok(new{ticket,parts,payments,events});
        });
        app.MapPost("/api/repairs",async (HttpContext ctx,RepairCreateRequest req)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            if(!ValidText(req.Device,120)||!ValidText(req.RepairType,150)||
               !ValidOptional(req.CustomerName,120)||!ValidOptional(req.CustomerContact,150)||
               !ValidOptional(req.SerialNumber,120)||!ValidOptional(req.Notes,2000)||
               !ValidMoney(req.QuotedTotal)||!ValidMoney(req.PaidUpfront)||req.PaidUpfront>req.QuotedTotal||
               req.IdempotencyKey==Guid.Empty)
                return Results.BadRequest("Invalid device, repair type, customer fields, quote or upfront payment.");
            var assignee=req.AssignedTo?.Trim();
            if(assignee is {Length:0})assignee=null;
            await using var c=await connect();
            await using var tx=await c.BeginTransactionAsync();
            await LockKey(c,(NpgsqlTransaction)tx,req.IdempotencyKey);
            await using(var duplicate=new NpgsqlCommand("""
                SELECT id,store_id,device,repair_type,quoted_total,paid_upfront
                FROM repair_tickets WHERE request_key=@key
                """,c,tx)){
                duplicate.Parameters.AddWithValue("key",req.IdempotencyKey);
                await using var r=await duplicate.ExecuteReaderAsync();
                if(await r.ReadAsync()){
                    if(r.GetString(1)!=who.Value.store)return Results.NotFound();
                    var same=r.GetString(2)==req.Device!.Trim()&&
                       r.GetString(3)==req.RepairType!.Trim()&&r.GetDecimal(4)==req.QuotedTotal&&
                       r.GetDecimal(5)==req.PaidUpfront;
                    return same?Results.Ok(new{id=r.GetInt64(0),duplicate=true})
                               :Results.Conflict("Idempotency key is already used for another repair.");
                }
            }
            if(assignee is not null && !await AvailableEmployee(c,(NpgsqlTransaction)tx,who.Value.store,assignee))
                return Results.BadRequest("Assigned employee must have access to this store.");
            await using var cmd=new NpgsqlCommand("""
                INSERT INTO repair_tickets(store_id,created_by,device,repair_type,customer_name,customer_contact,
                     serial_number,notes,quoted_total,paid_upfront,assigned_to,request_key)
                VALUES(@store,@employee,@device,@type,@customer,@contact,@serial,@notes,@quote,@paid,@assigned,@key)
                RETURNING id
                """,c,tx);
            cmd.Parameters.AddWithValue("store",who.Value.store);
            cmd.Parameters.AddWithValue("employee",who.Value.employee);
            cmd.Parameters.AddWithValue("device",req.Device!.Trim());
            cmd.Parameters.AddWithValue("type",req.RepairType!.Trim());
            cmd.Parameters.AddWithValue("customer",(object?)req.CustomerName?.Trim()??DBNull.Value);
            cmd.Parameters.AddWithValue("contact",(object?)req.CustomerContact?.Trim()??DBNull.Value);
            cmd.Parameters.AddWithValue("serial",(object?)req.SerialNumber?.Trim()??DBNull.Value);
            cmd.Parameters.AddWithValue("notes",(object?)req.Notes?.Trim()??DBNull.Value);
            cmd.Parameters.AddWithValue("quote",req.QuotedTotal);
            cmd.Parameters.AddWithValue("paid",req.PaidUpfront);
            cmd.Parameters.AddWithValue("key",req.IdempotencyKey);
            cmd.Parameters.Add("assigned",NpgsqlDbType.Text).Value=(object?)assignee??DBNull.Value;
            var id=(long)(await cmd.ExecuteScalarAsync()??throw new InvalidOperationException("Repair creation failed"));
            await Event(c,(NpgsqlTransaction)tx,id,who.Value.store,who.Value.employee,
                "checked_in",$"Repair checked in. Quoted {req.QuotedTotal:C}, paid upfront {req.PaidUpfront:C}.");
            await tx.CommitAsync();
            return Results.Created($"/api/repairs/{id}",new{id});
        });
        app.MapPut("/api/repairs/{id:long}/status",async (HttpContext ctx,long id,RepairStatusRequest req)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            if(!ValidStatus(req.Status))return Results.BadRequest("Unknown repair status.");
            await using var c=await connect();await using var tx=await c.BeginTransactionAsync();
            string? old;
            await using(var get=new NpgsqlCommand("SELECT status FROM repair_tickets WHERE id=@id AND store_id=@store FOR UPDATE",c,tx)){
                get.Parameters.AddWithValue("id",id);get.Parameters.AddWithValue("store",who.Value.store);
                old=(string?)await get.ExecuteScalarAsync();
            }
            if(old is null)return Results.NotFound();
            if(old==req.Status)return Results.Ok(new{updated=false,status=old});
            await using(var set=new NpgsqlCommand("""
                UPDATE repair_tickets SET status=@status,updated_at=now(),
                    closed_at=CASE WHEN @closed THEN now() ELSE NULL END
                WHERE id=@id AND store_id=@store
                """,c,tx)){
                set.Parameters.AddWithValue("status",req.Status!);
                set.Parameters.AddWithValue("closed",IsClosed(req.Status!));
                set.Parameters.AddWithValue("id",id);set.Parameters.AddWithValue("store",who.Value.store);
                await set.ExecuteNonQueryAsync();
            }
            await Event(c,(NpgsqlTransaction)tx,id,who.Value.store,who.Value.employee,
                "status",$"{old} → {req.Status}");
            await tx.CommitAsync();
            return Results.Ok(new{updated=true,status=req.Status});
        });
        app.MapPut("/api/repairs/{id:long}/assign",async (HttpContext ctx,long id,RepairAssignRequest req)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            var employee=req.EmployeeId?.Trim();if(employee is {Length:0})employee=null;
            await using var c=await connect();await using var tx=await c.BeginTransactionAsync();
            if(employee is not null&&!await AvailableEmployee(c,(NpgsqlTransaction)tx,who.Value.store,employee))
                return Results.BadRequest("Technician must be an active employee at this store.");
            await using(var cmd=new NpgsqlCommand("UPDATE repair_tickets SET assigned_to=@employee,updated_at=now() WHERE id=@id AND store_id=@store",c,tx)){
                cmd.Parameters.Add("employee",NpgsqlDbType.Text).Value=(object?)employee??DBNull.Value;
                cmd.Parameters.AddWithValue("id",id);cmd.Parameters.AddWithValue("store",who.Value.store);
                if(await cmd.ExecuteNonQueryAsync()==0)return Results.NotFound();
            }
            await Event(c,(NpgsqlTransaction)tx,id,who.Value.store,who.Value.employee,"assigned",
                employee is null?"Unassigned technician":"Assigned to "+employee);
            await tx.CommitAsync();return Results.Ok(new{assignedTo=employee});
        });
        app.MapPost("/api/repairs/{id:long}/payments",async (HttpContext ctx,long id,RepairPaymentRequest req)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            if(!ValidMoney(req.Amount)||req.Amount<=0||req.IdempotencyKey==Guid.Empty)
                return Results.BadRequest("A positive payment in cents and an idempotency key are required.");
            await using var c=await connect();await using var tx=await c.BeginTransactionAsync();
            await LockKey(c,(NpgsqlTransaction)tx,req.IdempotencyKey);
            await using(var duplicate=new NpgsqlCommand("SELECT repair_id,amount,store_id FROM repair_payments WHERE request_key=@key",c,tx)){
                duplicate.Parameters.AddWithValue("key",req.IdempotencyKey);
                await using var reader=await duplicate.ExecuteReaderAsync();
                if(await reader.ReadAsync()){
                    if(reader.GetString(2)!=who.Value.store)return Results.NotFound();
                    return reader.GetInt64(0)==id&&reader.GetDecimal(1)==req.Amount
                        ?Results.Ok(new{duplicate=true}):Results.Conflict("Idempotency key is already used.");
                }
            }
            decimal quote,paid;
            string status;
            await using(var lockRepair=new NpgsqlCommand("""
                SELECT quoted_total,paid_upfront,status FROM repair_tickets
                WHERE id=@id AND store_id=@store FOR UPDATE
                """,c,tx)){
                lockRepair.Parameters.AddWithValue("id",id);lockRepair.Parameters.AddWithValue("store",who.Value.store);
                await using var r=await lockRepair.ExecuteReaderAsync();
                if(!await r.ReadAsync())return Results.NotFound();
                quote=r.GetDecimal(0);paid=r.GetDecimal(1);status=r.GetString(2);
            }
            if(status=="cancelled")return Results.Conflict("Cancelled repair tickets cannot record payments.");
            await using(var total=new NpgsqlCommand("SELECT COALESCE(sum(amount),0) FROM repair_payments WHERE repair_id=@id",c,tx)){
                total.Parameters.AddWithValue("id",id);
                paid+=(decimal)(await total.ExecuteScalarAsync()??0m);
            }
            if(req.Amount>quote-paid)return Results.BadRequest("Payment exceeds the outstanding quoted balance.");
            await using(var insert=new NpgsqlCommand("""
                INSERT INTO repair_payments(repair_id,store_id,amount,employee_id,request_key)
                VALUES(@id,@store,@amount,@employee,@key)
                """,c,tx)){
                insert.Parameters.AddWithValue("id",id);
                insert.Parameters.AddWithValue("store",who.Value.store);
                insert.Parameters.AddWithValue("amount",req.Amount);
                insert.Parameters.AddWithValue("employee",who.Value.employee);
                insert.Parameters.AddWithValue("key",req.IdempotencyKey);
                await insert.ExecuteNonQueryAsync();
            }
            await Event(c,(NpgsqlTransaction)tx,id,who.Value.store,who.Value.employee,
                "payment",$"Payment recorded: {req.Amount:C}. No payment was processed by Inventory Manager.");
            await tx.CommitAsync();
            return Results.Ok(new{recorded=true,balanceDue=quote-paid-req.Amount});
        });
        app.MapPost("/api/repairs/{id:long}/parts",async (HttpContext ctx,long id,RepairPartRequest req)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            var code=req.Code?.Trim()??"";
            if(req.IdempotencyKey==Guid.Empty||req.Quantity<1||req.Quantity>10000||
               (req.ItemId is null&&code.Length==0)||code.Length>150)
                return Results.BadRequest("Choose a part or scan a barcode, quantity 1–10,000, and provide an idempotency key.");
            await using var c=await connect();await using var tx=await c.BeginTransactionAsync();
            await LockKey(c,(NpgsqlTransaction)tx,req.IdempotencyKey);
            // Duplicate requests are recognized before taking further stock.
            await using(var dup=new NpgsqlCommand("""
                SELECT p.id,p.repair_id,p.item_id,p.quantity,p.movement_id,p.store_id,i.sku,i.barcode
                FROM repair_parts p JOIN items i ON i.id=p.item_id WHERE p.request_key=@key
                """,c,tx)){
                dup.Parameters.AddWithValue("key",req.IdempotencyKey);
                await using var reader=await dup.ExecuteReaderAsync();
                if(await reader.ReadAsync()){
                    if(reader.GetString(5)!=who.Value.store)return Results.NotFound();
                    var existingId=reader.GetGuid(2);
                    var matchesItem=req.ItemId is not null?req.ItemId==existingId:
                        code.Length>0&&(code==reader.GetString(6)||
                          !reader.IsDBNull(7)&&code==reader.GetString(7));
                    var same=reader.GetInt64(1)==id&&reader.GetInt32(3)==req.Quantity&&matchesItem;
                    return same?Results.Ok(new{duplicate=true,partId=reader.GetInt64(0),movementId=reader.GetInt64(4)})
                               :Results.Conflict("Idempotency key belongs to a different part request.");
                }
            }
            string? status;
            await using(var lockRepair=new NpgsqlCommand("""
                SELECT status FROM repair_tickets WHERE id=@id AND store_id=@store FOR UPDATE
                """,c,tx)){
                lockRepair.Parameters.AddWithValue("id",id);lockRepair.Parameters.AddWithValue("store",who.Value.store);
                status=(string?)await lockRepair.ExecuteScalarAsync();
            }
            if(status is null)return Results.NotFound();
            if(IsClosed(status))return Results.Conflict("Reopen the repair before adding parts.");
            Guid itemId;string itemName;int stock;
            await using(var get=new NpgsqlCommand("""
                SELECT id,name,quantity FROM items
                WHERE store_id=@store AND deleted_at IS NULL
                  AND ((@item::uuid IS NOT NULL AND id=@item)
                       OR (@item::uuid IS NULL AND @code<>'' AND (sku=@code OR barcode=@code)))
                ORDER BY CASE WHEN sku=@code THEN 0 ELSE 1 END LIMIT 1 FOR UPDATE
                """,c,tx)){
                get.Parameters.AddWithValue("store",who.Value.store);
                get.Parameters.Add("item",NpgsqlDbType.Uuid).Value=(object?)req.ItemId??DBNull.Value;
                get.Parameters.AddWithValue("code",code);
                await using var reader=await get.ExecuteReaderAsync();
                if(!await reader.ReadAsync())return Results.NotFound("Part not found in this store's active inventory.");
                itemId=reader.GetGuid(0);itemName=reader.GetString(1);stock=reader.GetInt32(2);
            }
            if(stock<req.Quantity)return Results.BadRequest("Insufficient stock. Part usage was not recorded.");
            await using(var update=new NpgsqlCommand("UPDATE items SET quantity=quantity-@qty,updated_at=now() WHERE id=@item AND store_id=@store",c,tx)){
                update.Parameters.AddWithValue("qty",req.Quantity);
                update.Parameters.AddWithValue("item",itemId);update.Parameters.AddWithValue("store",who.Value.store);
                await update.ExecuteNonQueryAsync();
            }
            long movementId;
            await using(var insert=new NpgsqlCommand("""
                INSERT INTO movements(item_id,delta,before_qty,after_qty,note,idempotency_key,store_id,employee_id)
                VALUES(@item,@delta,@before,@after,@note,@key,@store,@employee) RETURNING id
                """,c,tx)){
                insert.Parameters.AddWithValue("item",itemId);
                insert.Parameters.AddWithValue("delta",-req.Quantity);
                insert.Parameters.AddWithValue("before",stock);
                insert.Parameters.AddWithValue("after",stock-req.Quantity);
                insert.Parameters.AddWithValue("note",$"Repair #{id}: part used ({itemName})");
                insert.Parameters.AddWithValue("key",req.IdempotencyKey);
                insert.Parameters.AddWithValue("store",who.Value.store);
                insert.Parameters.AddWithValue("employee",who.Value.employee);
                movementId=(long)(await insert.ExecuteScalarAsync()??throw new InvalidOperationException("Stock movement missing"));
            }
            long partId;
            await using(var insert=new NpgsqlCommand("""
                INSERT INTO repair_parts(repair_id,store_id,item_id,quantity,scanned_by,movement_id,request_key)
                VALUES(@repair,@store,@item,@qty,@employee,@movement,@key) RETURNING id
                """,c,tx)){
                insert.Parameters.AddWithValue("repair",id);
                insert.Parameters.AddWithValue("store",who.Value.store);
                insert.Parameters.AddWithValue("item",itemId);
                insert.Parameters.AddWithValue("qty",req.Quantity);
                insert.Parameters.AddWithValue("employee",who.Value.employee);
                insert.Parameters.AddWithValue("movement",movementId);
                insert.Parameters.AddWithValue("key",req.IdempotencyKey);
                partId=(long)(await insert.ExecuteScalarAsync()??throw new InvalidOperationException("Part row missing"));
            }
            await Event(c,(NpgsqlTransaction)tx,id,who.Value.store,who.Value.employee,
                "part_used",$"{req.Quantity} × {itemName} (movement #{movementId})");
            await queueLowStock(c,(NpgsqlTransaction)tx,itemId,who.Value.store);
            await tx.CommitAsync();
            return Results.Ok(new{partId,movementId,itemId,itemName,quantity=req.Quantity,after=stock-req.Quantity,duplicate=false});
        });
        app.MapPost("/api/repairs/{id:long}/parts/{partId:long}/return",async (HttpContext ctx,long id,long partId,RepairReturnRequest req)=>{
            var who=await identity(ctx);if(who is null)return Results.Unauthorized();
            if(req.IdempotencyKey==Guid.Empty)return Results.BadRequest("Idempotency key required.");
            await using var c=await connect();await using var tx=await c.BeginTransactionAsync();
            await LockKey(c,(NpgsqlTransaction)tx,req.IdempotencyKey);
            // Lock the ticket first, as with part consumption, to maintain lock ordering.
            await using(var repair=new NpgsqlCommand("SELECT id FROM repair_tickets WHERE id=@id AND store_id=@store FOR UPDATE",c,tx)){
                repair.Parameters.AddWithValue("id",id);repair.Parameters.AddWithValue("store",who.Value.store);
                if(await repair.ExecuteScalarAsync() is null)return Results.NotFound();
            }
            Guid itemId;int quantity;DateTime? returnedAt;Guid? returnKey;long? priorMovement;
            await using(var get=new NpgsqlCommand("""
                SELECT item_id,quantity,returned_at,return_key,return_movement_id
                FROM repair_parts WHERE id=@part AND repair_id=@repair AND store_id=@store FOR UPDATE
                """,c,tx)){
                get.Parameters.AddWithValue("part",partId);get.Parameters.AddWithValue("repair",id);
                get.Parameters.AddWithValue("store",who.Value.store);
                await using var reader=await get.ExecuteReaderAsync();
                if(!await reader.ReadAsync())return Results.NotFound();
                itemId=reader.GetGuid(0);quantity=reader.GetInt32(1);
                returnedAt=reader.IsDBNull(2)?null:reader.GetDateTime(2);
                returnKey=reader.IsDBNull(3)?null:reader.GetGuid(3);
                priorMovement=reader.IsDBNull(4)?null:reader.GetInt64(4);
            }
            if(returnedAt is not null)
                return returnKey==req.IdempotencyKey
                    ?Results.Ok(new{duplicate=true,movementId=priorMovement})
                    :Results.Conflict("Part has already been returned to inventory.");
            int stock;
            await using(var get=new NpgsqlCommand("SELECT quantity FROM items WHERE id=@item AND store_id=@store FOR UPDATE",c,tx)){
                get.Parameters.AddWithValue("item",itemId);get.Parameters.AddWithValue("store",who.Value.store);
                var result=await get.ExecuteScalarAsync();
                if(result is null)return Results.Conflict("Original inventory product was removed.");
                stock=(int)result;
            }
            if((long)stock+quantity>int.MaxValue)return Results.BadRequest("Stock quantity overflow.");
            await using(var update=new NpgsqlCommand("UPDATE items SET quantity=quantity+@qty,updated_at=now() WHERE id=@item AND store_id=@store",c,tx)){
                update.Parameters.AddWithValue("qty",quantity);update.Parameters.AddWithValue("item",itemId);
                update.Parameters.AddWithValue("store",who.Value.store);
                await update.ExecuteNonQueryAsync();
            }
            long movementId;
            await using(var insert=new NpgsqlCommand("""
                INSERT INTO movements(item_id,delta,before_qty,after_qty,note,idempotency_key,store_id,employee_id)
                VALUES(@item,@delta,@before,@after,@note,@key,@store,@employee) RETURNING id
                """,c,tx)){
                insert.Parameters.AddWithValue("item",itemId);
                insert.Parameters.AddWithValue("delta",quantity);
                insert.Parameters.AddWithValue("before",stock);
                insert.Parameters.AddWithValue("after",stock+quantity);
                insert.Parameters.AddWithValue("note",$"Repair #{id}: unused part returned (record #{partId})");
                insert.Parameters.AddWithValue("key",req.IdempotencyKey);
                insert.Parameters.AddWithValue("store",who.Value.store);insert.Parameters.AddWithValue("employee",who.Value.employee);
                movementId=(long)(await insert.ExecuteScalarAsync()??throw new InvalidOperationException("Return movement missing"));
            }
            await using(var update=new NpgsqlCommand("""
                UPDATE repair_parts SET returned_at=now(),returned_by=@employee,return_key=@key,
                  return_movement_id=@movement WHERE id=@part AND repair_id=@repair AND store_id=@store
                """,c,tx)){
                update.Parameters.AddWithValue("employee",who.Value.employee);
                update.Parameters.AddWithValue("key",req.IdempotencyKey);
                update.Parameters.AddWithValue("movement",movementId);
                update.Parameters.AddWithValue("part",partId);update.Parameters.AddWithValue("repair",id);
                update.Parameters.AddWithValue("store",who.Value.store);
                await update.ExecuteNonQueryAsync();
            }
            await Event(c,(NpgsqlTransaction)tx,id,who.Value.store,who.Value.employee,
                "part_returned",$"{quantity} part(s) returned to inventory (movement #{movementId})");
            await queueLowStock(c,(NpgsqlTransaction)tx,itemId,who.Value.store);
            await tx.CommitAsync();
            return Results.Ok(new{returned=true,movementId});
        });
    }
}

record RepairCreateRequest(string? Device,string? RepairType,decimal QuotedTotal,decimal PaidUpfront,
    string? CustomerName,string? CustomerContact,string? SerialNumber,string? Notes,string? AssignedTo,Guid IdempotencyKey);
record RepairStatusRequest(string? Status);
record RepairAssignRequest(string? EmployeeId);
record RepairPaymentRequest(decimal Amount,Guid IdempotencyKey);
record RepairPartRequest(Guid? ItemId,string? Code,int Quantity,Guid IdempotencyKey);
record RepairReturnRequest(Guid IdempotencyKey);
