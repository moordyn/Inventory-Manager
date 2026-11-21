// Tests run against a disposable PostgreSQL 17 database and the .NET API.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

const base=process.env.TEST_API_URL||'http://127.0.0.1:18424';
const setupToken=process.env.SETUP_TOKEN;
assert.ok(setupToken,'SETUP_TOKEN must be set');
const pin='682951';

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

const setup=await call('auth/setup',{method:'POST',body:{setupToken,pin,confirmPin:pin},expect:200});
assert.equal(setup.body.created,true);
const login=(await call('auth/login',{method:'POST',body:{employeeId:'OWNER',pin},expect:200})).body;
assert.match(login.token,/^[0-9A-F]{64}$/i);
const admin=login.token;
assert.equal((await call('auth/me',{token:admin})).body.isGlobalAdmin,true);

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

const employeeToken=(await call('auth/employee-login',{method:'POST',body:{employeeId:'TESTUSER'},expect:200})).body.token;
await call('admin/audit',{token:employeeToken,expect:403});
await call('auth/select-store',{token:employeeToken,method:'POST',body:{storeId:'BRANCH2'},expect:200});
const otherCsv=await call('items/export',{token:employeeToken});
assert.ok(!otherCsv.body.includes('Test charging kit'),'CSV must only include current store');
await call('items/'+id+'/history',{token:employeeToken,expect:404});

await call('admin/employees/TESTUSER/stores/DEFAULT',{token:admin,method:'DELETE',expect:400});
await call('admin/employees/TESTUSER/stores/BRANCH2',{token:admin,method:'DELETE',expect:200});
const revoked=await call('auth/select-store',{token:employeeToken,method:'POST',body:{storeId:'BRANCH2'},expect:401});
assert.ok([401,403].includes(revoked.status),'Revoked sessions must not retain branch access');
audit=(await call('admin/audit',{token:admin})).body;
assert.ok(audit.some(x=>x.action==='access.revoked'&&x.storeId==='BRANCH2'));

await call('admin/employees/TESTUSER/permissions',{token:admin,method:'PUT',body:{globalAdmin:false,storeAdminIds:['DEFAULT']},expect:200});
const storeAdmin=(await call('auth/employee-login',{method:'POST',body:{employeeId:'TESTUSER'},expect:200})).body.token;
const scoped=(await call('admin/audit',{token:storeAdmin})).body;
assert.ok(scoped.every(x=>x.storeId==='DEFAULT'),'Store admins only see their own location activity');
await call('admin/stores',{token:storeAdmin,method:'POST',body:{id:'NOPE',name:'Should not be created'},expect:403});

await call('items/'+id+'/delete',{token:admin,method:'POST',body:{confirmation:'TEST CHARGING KIT'},expect:200});
audit=(await call('admin/audit',{token:admin})).body;
assert.ok(audit.some(x=>x.action==='product.deleted'&&x.target===id));
assert.ok(!(await call('items/export',{token:admin})).body.includes('Test charging kit'));
console.log('API smoke tests passed: CSV, reorder, product history, idempotency, audit, scopes and deletion.');
