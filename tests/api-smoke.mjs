// Tests run against a disposable PostgreSQL 17 database and the .NET API.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';

const base=process.env.TEST_API_URL||'http://127.0.0.1:18424';
const setupToken=process.env.SETUP_TOKEN;
assert.ok(setupToken,'SETUP_TOKEN must be set');
const ownerPassword='InitialOwner1!';
const employeePassword='EmployeeSetup1!';
const senderOutbox=process.env.AUTH_EMAIL_TEST_OUTBOX;
assert.ok(senderOutbox,'AUTH_EMAIL_TEST_OUTBOX must be set for integration tests');

async function call(path,{method='GET',token,body,expect}={}){
  const response=await fetch(base+'/api/'+path,{
    method,
    headers:{...(token?{Authorization:'Bearer '+token}:{}),...(body!==undefined?{'Content-Type':'application/json'}:{})},
    ...(body!==undefined?{body:JSON.stringify(body)}:{})
  });
  const text=await response.text();
  if(expect!==undefined)assert.equal(response.status,expect,method+' '+path+': unexpected status '+response.status+' '+text.slice(0,500));
  else assert.ok(response.ok,method+' '+path+': '+response.status+' '+text.slice(0,500));
  const type=response.headers.get('content-type')||'';
  return {status:response.status,body:type.includes('json')&&text?JSON.parse(text):text,headers:response.headers};
}

function latestEmail(address,subjectPart){
 const rows=readFileSync(senderOutbox,'utf8').trim().split(/\r?\n/).filter(Boolean).map(line=>JSON.parse(line));
 const match=rows.reverse().find(row=>row.email===address&&row.subject.includes(subjectPart));
 assert.ok(match,'Missing captured account email for '+address+' subject '+subjectPart);
 const code=match.body.match(/authToken=([0-9A-F]{64})/);
 assert.ok(code,'Expected secure one-time token link in email');
 return code[1];
}
const ownerEmail='owner@example.com';
const setup=await call('auth/setup',{method:'POST',body:{
 setupToken,email:ownerEmail,confirmEmail:ownerEmail,password:ownerPassword,confirmPassword:ownerPassword
},expect:200});
assert.equal(setup.body.created,true);
await call('auth/login',{method:'POST',body:{employeeId:'OWNER',pin:'682951'},expect:410});
const login=(await call('auth/employee-login',{method:'POST',body:{employeeId:'OWNER',password:ownerPassword},expect:200})).body;
assert.match(login.token,/^[0-9A-F]{64}$/i);
const admin=login.token;
assert.equal((await call('auth/me',{token:admin})).body.isGlobalAdmin,true);
assert.equal((await call('auth/me',{token:admin})).body.emailVerified,false);
// Five themes are persisted per employee; only global admins choose the public login palette.
assert.equal((await call('auth/status')).body.loginTheme,'mint');
for(const theme of ['mint','purple','ocean','ember','rose','slate','forest','gold','crimson','glacier']){
 await call('auth/theme',{token:admin,method:'PUT',body:{theme},expect:200});
 assert.equal((await call('auth/me',{token:admin})).body.theme,theme);
}
await call('auth/theme',{token:admin,method:'PUT',body:{theme:'invalid'},expect:400});
await call('auth/theme',{token:admin,method:'PUT',body:{theme:'purple'},expect:200});
assert.equal((await call('auth/me',{token:admin})).body.theme,'purple');
await call('admin/theme/login',{token:admin,method:'PUT',body:{theme:'glacier'},expect:200});
assert.equal((await call('auth/status')).body.loginTheme,'glacier');
await call('admin/theme/login',{token:admin,method:'PUT',body:{theme:'ocean'},expect:200});
assert.equal((await call('auth/status')).body.loginTheme,'ocean');
await call('admin/theme/login',{token:admin,method:'PUT',body:{theme:'invalid'},expect:400});

await call('auth/send-verification',{token:admin,method:'POST',expect:200});
const ownerCode=latestEmail(ownerEmail,'Verify your Inventory Manager email');
assert.equal((await call('auth/token-info',{method:'POST',body:{token:ownerCode},expect:200})).body.purpose,'verify');
await call('auth/complete-token',{method:'POST',body:{token:ownerCode},expect:200});
await call('auth/complete-token',{method:'POST',body:{token:ownerCode},expect:400});
assert.equal((await call('auth/me',{token:admin})).body.emailVerified,true);
assert.equal((await call('auth/activity',{token:admin,method:'POST',expect:200})).body.renewed,true);

const sku='=2+2';
const created=await call('items',{token:admin,method:'POST',body:{sku,name:'Test charging kit',quantity:4,minStock:4,unitCost:12.5,category:'Accessories',barcode:'701233100200',description:'Stock test'},expect:201});
const id=created.body.id;
assert.match(id,/^[0-9a-f-]{36}$/i);
let low=(await call('items/reorder',{token:admin})).body;
assert.equal(low.length,1);
assert.equal(low[0].sku,sku);
assert.equal(low[0].reorderQty,1,'At the threshold, order one to exit low-stock status');

const inventoryCsv=await call('items/export',{token:admin});
assert.match(inventoryCsv.headers.get('content-type'),/text\/csv/);
assert.ok(inventoryCsv.body.includes('"\'=2+2"'),'CSV must escape spreadsheet formulas');
assert.ok(inventoryCsv.body.includes('Test charging kit'));
const reorderCsv=await call('items/reorder/export',{token:admin});
assert.ok(reorderCsv.body.includes('Order Quantity (to exceed minimum)'));
assert.ok(reorderCsv.body.includes('"\'=2+2"'));

let movements=(await call('items/'+id+'/history',{token:admin})).body;
assert.equal(movements.length,1);
assert.equal(movements[0].delta,4);
assert.equal(movements[0].note,'Initial stock');

const idempotencyKey=randomUUID();
const receive=await call('items/'+id+'/movements',{token:admin,method:'POST',body:{delta:5,note:'Restock received',idempotencyKey},expect:200});
assert.equal(receive.body.after,9);
const repeat=await call('items/'+id+'/movements',{token:admin,method:'POST',body:{delta:5,note:'Restock received',idempotencyKey},expect:200});
assert.equal(repeat.body.duplicate,true);
await call('items/'+id+'/movements',{token:admin,method:'POST',body:{delta:-1,note:'Retail sale',idempotencyKey:randomUUID()},expect:200});
movements=(await call('items/'+id+'/history',{token:admin})).body;
assert.equal(movements.length,3,'Idempotent retries cannot duplicate movement history');
assert.equal(movements[0].delta,-1);
assert.equal(movements[0].employeeId,'OWNER');
// A checkout can receive a follow-up note without a new stock movement.
const ownerCheckoutId=movements[0].id;
assert.equal(movements[0].noteRevision,0);
const savedNote=await call('movements/'+ownerCheckoutId+'/note',{token:admin,method:'PUT',
 body:{note:'Damaged during repair',expectedRevision:0},expect:200});
assert.equal(savedNote.body.note,'Damaged during repair');
assert.equal(savedNote.body.noteRevision,1);
assert.equal(savedNote.body.noteUpdatedBy,'OWNER');
assert.ok(savedNote.body.noteUpdatedAt);
movements=(await call('items/'+id+'/history',{token:admin})).body;
assert.equal(movements.length,3,'Note edits do not create an extra movement');
assert.equal(movements[0].delta,-1,'Note edits do not alter the stock delta');
assert.equal(movements[0].after,8,'Note edits do not alter stock balance');
assert.equal(movements[0].note,'Damaged during repair');
assert.equal(movements[0].noteRevision,1);
const noteHistory=(await call('movements/'+ownerCheckoutId+'/note-edits',{token:admin})).body;
assert.equal(noteHistory.length,1);
assert.equal(noteHistory[0].previousNote,'Retail sale');
assert.equal(noteHistory[0].note,'Damaged during repair');
await call('movements/'+ownerCheckoutId+'/note',{token:admin,method:'PUT',
 body:{note:'Outdated overwrite',expectedRevision:0},expect:409});
await call('movements/'+ownerCheckoutId+'/note',{token:admin,method:'PUT',
 body:{note:'   ',expectedRevision:1},expect:400});
await call('movements/'+ownerCheckoutId+'/note',{token:admin,method:'PUT',
 body:{note:'x'.repeat(1001),expectedRevision:1},expect:400});
await call('movements/'+ownerCheckoutId+'/note',{method:'PUT',
 body:{note:'Unauthorized',expectedRevision:1},expect:401});
await call('movements/'+movements[2].id+'/note',{token:admin,method:'PUT',
 body:{note:'Cannot edit check-in',expectedRevision:0},expect:404});
assert.equal((await call('movements',{token:admin})).body.find(x=>x.id===ownerCheckoutId).note,'Damaged during repair');
low=(await call('items/reorder',{token:admin})).body;
assert.equal(low.length,0,'Restocked item leaves reorder list');

await call('admin/stores',{token:admin,method:'POST',body:{id:'BRANCH2',name:'Other Branch'},expect:200});
await call('admin/employees',{token:admin,method:'POST',body:{storeId:'DEFAULT',id:'TESTUSER',name:'Test Employee'},expect:200});
await call('admin/employees/TESTUSER/stores',{token:admin,method:'POST',body:{storeId:'BRANCH2'},expect:200});
let audit=(await call('admin/audit',{token:admin})).body;
assert.ok(audit.some(x=>x.action==='store.created'));
assert.ok(audit.some(x=>x.action==='employee.created'));
assert.ok(audit.some(x=>x.action==='access.granted'&&x.target==='TESTUSER'));
assert.ok(audit.some(x=>x.employeeId==='OWNER'));

// Employee IDs no longer provide access until email is verified and a password is created.
await call('auth/employee-login',{method:'POST',body:{employeeId:'TESTUSER',password:'Unclaimed1!'},expect:409});
await call('auth/start-activation',{method:'POST',body:{
 employeeId:'TESTUSER',email:'test.employee@example.com',confirmEmail:'different@example.com',
 password:employeePassword,confirmPassword:employeePassword},expect:400});
await call('auth/start-activation',{method:'POST',body:{
 employeeId:'TESTUSER',email:'test.employee@example.com',confirmEmail:'test.employee@example.com',
 password:'weak',confirmPassword:'weak'},expect:400});
await call('auth/start-activation',{method:'POST',body:{
 employeeId:'TESTUSER',email:'test.employee@example.com',confirmEmail:'test.employee@example.com',
 password:employeePassword,confirmPassword:employeePassword},expect:200});
const activateCode=latestEmail('test.employee@example.com','Verify your Inventory Manager account');
const activationPreview=await call('auth/token-info',{method:'POST',body:{token:activateCode},expect:200});
assert.equal(activationPreview.body.purpose,'activate');
await call('auth/complete-token',{method:'POST',body:{token:activateCode},expect:200});
await call('auth/complete-token',{method:'POST',body:{token:activateCode},expect:400});
let employeeToken=(await call('auth/employee-login',{method:'POST',body:{employeeId:'TESTUSER',password:employeePassword},expect:200})).body.token;
assert.equal((await call('auth/me',{token:employeeToken})).body.emailVerified,true);
await call('admin/theme/login',{token:employeeToken,method:'PUT',body:{theme:'rose'},expect:403});
await call('auth/theme',{token:employeeToken,method:'PUT',body:{theme:'ember'},expect:200});
assert.equal((await call('auth/me',{token:employeeToken})).body.theme,'ember');

// Account email changes only activate after proving possession of the new mailbox.
await call('auth/change-email',{token:employeeToken,method:'POST',body:{
  email:'updated.employee@example.com',confirmEmail:'different@example.com',password:employeePassword
},expect:400});
await call('auth/change-email',{token:employeeToken,method:'POST',body:{
  email:'updated.employee@example.com',confirmEmail:'updated.employee@example.com',password:'wrong-password'
},expect:403});
await call('auth/change-email',{token:employeeToken,method:'POST',body:{
  email:'updated.employee@example.com',confirmEmail:'updated.employee@example.com',password:employeePassword
},expect:200});
assert.equal((await call('auth/me',{token:employeeToken})).body.email,'test.employee@example.com');
const emailChangeCode=latestEmail('updated.employee@example.com','Confirm your new Inventory Manager email');
assert.equal((await call('auth/token-info',{method:'POST',body:{token:emailChangeCode},expect:200})).body.purpose,'change_email');
await call('auth/complete-token',{method:'POST',body:{token:emailChangeCode},expect:200});
await call('auth/complete-token',{method:'POST',body:{token:emailChangeCode},expect:400});
await call('auth/me',{token:employeeToken,expect:401});
employeeToken=(await call('auth/employee-login',{method:'POST',body:{
  employeeId:'TESTUSER',password:employeePassword
},expect:200})).body.token;
assert.equal((await call('auth/me',{token:employeeToken})).body.email,'updated.employee@example.com');
await call('admin/employees/TESTUSER/id',{token:employeeToken,method:'PUT',body:{newId:'OTHERUSER'},expect:403});

// Any employee with access to the store can add context to a coworker's checkout.
const coworkerEdit=await call('movements/'+ownerCheckoutId+'/note',{token:employeeToken,method:'PUT',
 body:{note:'Damaged during repair; display damage confirmed',expectedRevision:1},expect:200});
assert.equal(coworkerEdit.body.noteRevision,2);
assert.equal(coworkerEdit.body.noteUpdatedBy,'TESTUSER');
const coworkerNoteEdits=(await call('movements/'+ownerCheckoutId+'/note-edits',{token:employeeToken})).body;
assert.equal(coworkerNoteEdits.length,2);
assert.equal(coworkerNoteEdits[0].employeeId,'TESTUSER');
assert.equal(coworkerNoteEdits[1].employeeId,'OWNER');
await call('movements/'+ownerCheckoutId+'/note',{token:admin,method:'PUT',
 body:{note:'Manager overwrote outdated version',expectedRevision:1},expect:409});
const employeeCheckout=await call('items/'+id+'/movements',{token:employeeToken,method:'POST',
 body:{delta:-1,note:'Used for display repair',idempotencyKey:randomUUID()},expect:200});
assert.equal(employeeCheckout.body.after,7);
const newMovements=(await call('items/'+id+'/history',{token:admin})).body;
const employeeCheckoutId=newMovements[0].id;
assert.equal(newMovements[0].employeeId,'TESTUSER');
await call('movements/'+employeeCheckoutId+'/note',{token:employeeToken,method:'PUT',
 body:{note:'Damaged during 14 Pro Max repair',expectedRevision:0},expect:200});
await call('movements/'+employeeCheckoutId+'/note',{token:admin,method:'PUT',
 body:{note:'Damaged during 14 Pro Max repair; manager reviewed',expectedRevision:1},expect:200});
assert.equal((await call('movements/'+employeeCheckoutId+'/note-edits',{token:employeeToken})).body.length,2);

await call('admin/audit',{token:employeeToken,expect:403});
await call('auth/select-store',{token:employeeToken,method:'POST',body:{storeId:'BRANCH2'},expect:200});
const otherCsv=await call('items/export',{token:employeeToken});
assert.ok(!otherCsv.body.includes('Test charging kit'),'CSV must only include current store');
await call('items/'+id+'/history',{token:employeeToken,expect:404});
await call('movements/'+ownerCheckoutId+'/note-edits',{token:employeeToken,expect:404});
await call('movements/'+employeeCheckoutId+'/note',{token:employeeToken,method:'PUT',
 body:{note:'Cross-store edit blocked',expectedRevision:2},expect:404});

await call('admin/employees/TESTUSER/stores/DEFAULT',{token:admin,method:'DELETE',expect:400});
await call('admin/employees/TESTUSER/stores/BRANCH2',{token:admin,method:'DELETE',expect:200});
const revoked=await call('auth/select-store',{token:employeeToken,method:'POST',body:{storeId:'BRANCH2'},expect:401});
assert.ok([401,403].includes(revoked.status),'Revoked sessions must not retain branch access');
audit=(await call('admin/audit',{token:admin})).body;
assert.ok(audit.some(x=>x.action==='access.revoked'&&x.storeId==='BRANCH2'));

await call('admin/employees/TESTUSER/permissions',{token:admin,method:'PUT',body:{globalAdmin:false,storeAdminIds:['DEFAULT']},expect:200});
const storeAdmin=(await call('auth/employee-login',{method:'POST',body:{employeeId:'TESTUSER',password:employeePassword},expect:200})).body.token;
// Store admins can rename themselves and ordinary members of their store,
// but not another store/global admin; global admins can rename anyone.
await call('admin/employees/OWNER/name',{token:storeAdmin,method:'PUT',body:{name:'Blocked Owner Rename'},expect:403});
await call('admin/employees/TESTUSER/name',{token:storeAdmin,method:'PUT',body:{name:'Store Lead'},expect:200});
assert.equal((await call('auth/me',{token:storeAdmin})).body.employeeName,'Store Lead');
await call('admin/employees/OWNER/name',{token:admin,method:'PUT',body:{name:'Company Owner'},expect:200});
assert.equal((await call('auth/me',{token:admin})).body.employeeName,'Company Owner');
await call('admin/employees/TESTUSER/name',{token:admin,method:'PUT',body:{name:'Test Employee Renamed'},expect:200});
assert.equal((await call('auth/me',{token:storeAdmin})).body.employeeName,'Test Employee Renamed');
await call('admin/employees/TESTUSER/name',{token:storeAdmin,method:'PUT',body:{name:' '},expect:400});
const scoped=(await call('admin/audit',{token:storeAdmin})).body;
assert.ok(scoped.every(x=>x.storeId==='DEFAULT'),'Store admins only see their own location activity');
await call('admin/stores',{token:storeAdmin,method:'POST',body:{id:'NOPE',name:'Should not be created'},expect:403});

// Only global administrators can edit IDs. Preserve store access and transaction ownership.
await call('admin/employees/TESTUSER/id',{token:storeAdmin,method:'PUT',body:{newId:'RENAMEDUSER'},expect:403});
await call('admin/employees/TESTUSER/id',{token:admin,method:'PUT',body:{newId:'OWNER'},expect:409});
await call('admin/employees/TESTUSER/id',{token:admin,method:'PUT',body:{newId:'RENAMEDUSER'},expect:200});
await call('auth/me',{token:storeAdmin,expect:401});
await call('auth/employee-login',{method:'POST',body:{employeeId:'TESTUSER',password:employeePassword},expect:401});
const renamedLogin=(await call('auth/employee-login',{method:'POST',body:{employeeId:'RENAMEDUSER',password:employeePassword},expect:200})).body.token;
assert.equal((await call('auth/me',{token:renamedLogin})).body.employeeId,'RENAMEDUSER');
assert.equal((await call('auth/me',{token:renamedLogin})).body.theme,'ember','Theme preference must survive employee ID changes');
const renamedHistory=(await call('items/'+id+'/history',{token:admin})).body;
assert.ok(renamedHistory.some(x=>x.employeeId==='RENAMEDUSER'));
const renamedAccess=(await call('admin/employee-access',{token:admin})).body;
assert.ok(renamedAccess.some(x=>x.employeeId==='RENAMEDUSER'));
assert.equal((await call('auth/me',{token:renamedLogin})).body.employeeName,'Test Employee Renamed');
const renamedToken=renamedLogin;

// Forgot-password recovery goes only to the already-verified email and invalidates old sessions.
const unknownReset=await call('auth/forgot-password',{method:'POST',body:{employeeId:'UNKNOWNUSER'},expect:200});
const knownReset=await call('auth/forgot-password',{method:'POST',body:{employeeId:'RENAMEDUSER'},expect:200});
assert.equal(unknownReset.body.message,knownReset.body.message,'Reset response must not expose whether an account exists');
const resetCode=latestEmail('updated.employee@example.com','Reset your Inventory Manager password');
await call('auth/complete-token',{method:'POST',body:{token:resetCode,password:'short',confirmPassword:'short'},expect:400});
const resetPassword='ChangedPassword2!';
await call('auth/complete-token',{method:'POST',body:{token:resetCode,password:resetPassword,confirmPassword:resetPassword},expect:200});
await call('auth/complete-token',{method:'POST',body:{token:resetCode,password:resetPassword,confirmPassword:resetPassword},expect:400});
await call('auth/me',{token:renamedToken,expect:401});
await call('auth/me',{token:employeeToken,expect:401});
await call('auth/employee-login',{method:'POST',body:{employeeId:'RENAMEDUSER',password:employeePassword},expect:401});
employeeToken=(await call('auth/employee-login',{method:'POST',body:{employeeId:'RENAMEDUSER',password:resetPassword},expect:200})).body.token;
assert.ok(employeeToken,'New password should permit login');

// Repair queue integration: per-store FIFO, immutable check-in attribution,
// exactly-once multiple part deductions, payment balance and part reversal.
await call('repairs',{expect:401});
await call('repairs',{token:admin,expect:200});
const createKey=randomUUID();
const repairBody={
 device:'iPhone 14 Pro',repairType:'Soft OLED replacement',quotedTotal:120,paidUpfront:30,
 customerName:'Example Customer',customerContact:'555-0100',serialNumber:'TEST-SERIAL-1',
 notes:'Replace display and test touch',assignedTo:'RENAMEDUSER',idempotencyKey:createKey
};
await call('repairs',{token:admin,method:'POST',body:{...repairBody,paidUpfront:150,idempotencyKey:randomUUID()},expect:400});
const createdRepair=(await call('repairs',{token:admin,method:'POST',body:repairBody,expect:201})).body.id;
const retryRepair=(await call('repairs',{token:admin,method:'POST',body:repairBody,expect:200})).body;
assert.equal(retryRepair.id,createdRepair);
assert.equal(retryRepair.duplicate,true,'Network retry must not create another ticket');
await call('repairs',{token:admin,method:'POST',body:{...repairBody,device:'Other device'},expect:409});
const laterRepair=(await call('repairs',{token:admin,method:'POST',body:{
 device:'iPad Pro',repairType:'Charging port',quotedTotal:80,paidUpfront:0,idempotencyKey:randomUUID()
},expect:201})).body.id;
let repairQueue=(await call('repairs',{token:admin})).body;
assert.deepEqual(repairQueue.map(r=>r.id),[createdRepair,laterRepair],'Active repairs are oldest first');
assert.equal(repairQueue[0].createdBy,'OWNER');
assert.equal(repairQueue[0].paidUpfront,30);
assert.equal(repairQueue[0].balanceDue,90);
assert.ok((await call('repairs/technicians',{token:admin})).body.some(x=>x.id==='RENAMEDUSER'));
let detail=(await call('repairs/'+createdRepair,{token:admin})).body;
assert.equal(detail.ticket.serialNumber,'TEST-SERIAL-1');
assert.equal(detail.ticket.createdBy,'OWNER');
assert.equal(detail.ticket.assignedTo,'RENAMEDUSER');
await call('repairs/'+createdRepair+'/status',{token:admin,method:'PUT',body:{status:'invalid'},expect:400});
await call('repairs/'+createdRepair+'/status',{token:admin,method:'PUT',body:{status:'in_progress'},expect:200});
await call('repairs/'+createdRepair+'/assign',{token:admin,method:'PUT',body:{employeeId:'NOT_EMPLOYEE'},expect:400});
await call('repairs/'+createdRepair+'/assign',{token:admin,method:'PUT',body:{employeeId:null},expect:200});
await call('repairs/'+createdRepair+'/assign',{token:admin,method:'PUT',body:{employeeId:'RENAMEDUSER'},expect:200});
const partKey1=randomUUID(),partKey2=randomUUID();
const consumed1=(await call('repairs/'+createdRepair+'/parts',{token:admin,method:'POST',body:{
 itemId:id,quantity:1,idempotencyKey:partKey1
},expect:200})).body;
assert.equal(consumed1.after,6);
const consumed2=(await call('repairs/'+createdRepair+'/parts',{token:admin,method:'POST',body:{
 code:'701233100200',quantity:2,idempotencyKey:partKey2
},expect:200})).body;
assert.equal(consumed2.after,4);
assert.equal((await call('repairs/'+createdRepair+'/parts',{token:admin,method:'POST',body:{
 code:'701233100200',quantity:2,idempotencyKey:partKey2
},expect:200})).body.duplicate,true);
await call('repairs/'+createdRepair+'/parts',{token:admin,method:'POST',body:{
 code:'BAD-SKU',quantity:2,idempotencyKey:partKey2
},expect:409});
await call('repairs/'+createdRepair+'/parts',{token:admin,method:'POST',body:{
 itemId:id,quantity:50,idempotencyKey:randomUUID()
},expect:400});
detail=(await call('repairs/'+createdRepair,{token:admin})).body;
assert.equal(detail.parts.length,2);
assert.equal(detail.parts.reduce((sum,p)=>sum+p.quantity,0),3);
assert.ok(detail.parts.every(p=>p.movementId>0));
assert.ok(detail.events.some(e=>e.kind==='part_used'));
const itemAfterParts=(await call('items',{token:admin})).body.find(x=>x.id===id);
assert.equal(itemAfterParts.quantity,4,'Two separately scanned parts must deduct exactly 3 units total');
assert.equal((await call('items/'+id+'/history',{token:admin})).body.filter(m=>
 String(m.note||'').includes('Repair #'+createdRepair)).length,2);
const returnKey=randomUUID();
assert.equal((await call('repairs/'+createdRepair+'/parts/'+consumed1.partId+'/return',{
 token:admin,method:'POST',body:{idempotencyKey:returnKey},expect:200
})).body.returned,true);
assert.equal((await call('repairs/'+createdRepair+'/parts/'+consumed1.partId+'/return',{
 token:admin,method:'POST',body:{idempotencyKey:returnKey},expect:200
})).body.duplicate,true);
await call('repairs/'+createdRepair+'/parts/'+consumed1.partId+'/return',{
 token:admin,method:'POST',body:{idempotencyKey:randomUUID()},expect:409
});
assert.equal((await call('items',{token:admin})).body.find(x=>x.id===id).quantity,5);
assert.equal((await call('repairs/'+createdRepair,{token:admin})).body.parts.filter(p=>!p.returnedAt).length,1);
const paymentKey=randomUUID();
assert.equal((await call('repairs/'+createdRepair+'/payments',{token:admin,method:'POST',body:{
 amount:40,idempotencyKey:paymentKey
},expect:200})).body.balanceDue,50);
assert.equal((await call('repairs/'+createdRepair+'/payments',{token:admin,method:'POST',body:{
 amount:40,idempotencyKey:paymentKey
},expect:200})).body.duplicate,true);
await call('repairs/'+createdRepair+'/payments',{token:admin,method:'POST',body:{
 amount:51,idempotencyKey:randomUUID()
},expect:400});
assert.equal((await call('repairs/'+createdRepair,{token:admin})).body.ticket.balanceDue,50);
await call('repairs/'+createdRepair+'/status',{token:admin,method:'PUT',body:{status:'completed'},expect:200});
await call('repairs/'+createdRepair+'/parts',{token:admin,method:'POST',body:{
 itemId:id,quantity:1,idempotencyKey:randomUUID()
},expect:409});
await call('repairs/'+laterRepair+'/status',{token:admin,method:'PUT',body:{status:'cancelled'},expect:200});
repairQueue=(await call('repairs',{token:admin})).body;
assert.equal(repairQueue.length,0);
assert.equal((await call('repairs?history=true',{token:admin})).body.length,2);
await call('auth/select-store',{token:admin,method:'POST',body:{storeId:'BRANCH2'},expect:200});
assert.equal((await call('repairs',{token:admin})).body.length,0);
await call('repairs/'+createdRepair,{token:admin,expect:404});
await call('repairs/'+createdRepair+'/status',{token:admin,method:'PUT',body:{status:'checked_in'},expect:404});
await call('auth/select-store',{token:admin,method:'POST',body:{storeId:'DEFAULT'},expect:200});

await call('items/'+id+'/delete',{token:admin,method:'POST',body:{confirmation:'TEST CHARGING KIT'},expect:200});
audit=(await call('admin/audit',{token:admin})).body;
assert.ok(audit.some(x=>x.action==='product.deleted'&&x.target===id));
assert.ok(!(await call('items/export',{token:admin})).body.includes('Test charging kit'));
// OWNER may rename their own account without losing global privileges or creating a new bootstrap.
await call('admin/employees/OWNER/id',{token:admin,method:'PUT',body:{newId:'PRIMARYADMIN'},expect:200});
await call('auth/me',{token:admin,expect:401});
assert.equal((await call('auth/status')).body.initialized,true);
await call('auth/employee-login',{method:'POST',body:{employeeId:'OWNER',password:ownerPassword},expect:401});
const newOwner=(await call('auth/employee-login',{method:'POST',body:{employeeId:'PRIMARYADMIN',password:ownerPassword},expect:200})).body.token;
assert.equal((await call('auth/me',{token:newOwner})).body.isGlobalAdmin,true);
await call('admin/employees/PRIMARYADMIN/permissions',{token:newOwner,method:'PUT',body:{globalAdmin:false,storeAdminIds:[]},expect:400});
assert.ok((await call('admin/employees',{token:newOwner})).body.some(e=>e.id==='PRIMARYADMIN'));
console.log('API smoke tests passed: five themes, login palette authorization, persistence, password login, first-time email verification, one-use tokens, password reset/session revocation, CSV, checkout notes, store permissions and audit.');
