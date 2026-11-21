// Verify checkout-note affordances against the actual browser code without a browser runtime.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const html=readFileSync('server/Inventory.Api/wwwroot/index.html','utf8');
const script=html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
assert.ok(script,'Missing application JavaScript');

function extract(signature){
  const start=script.indexOf(signature);
  assert.ok(start>=0,'Missing '+signature);
  let i=script.indexOf('{',start),depth=0;
  for(let k=i;k<script.length;k++){
    if(script[k]==='{')depth++;
    else if(script[k]==='}'){
      depth--;
      if(depth===0)return script.slice(start,k+1);
    }
  }
  throw Error('Unclosed function '+signature);
}

const scope={
  identity:{employeeId:'REPAIR_TECH',isAdmin:false},
  esc(value){return String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));},
  time(value){return String(value);}
};
vm.createContext(scope);
vm.runInContext(
  extract('function canEditCheckoutNote(entry)')+'\n'+extract('function noteCellMarkup(entry)'),
  scope
);
const checkout={id:845,delta:-1,employeeId:'REPAIR_TECH',note:'Repair item',noteRevision:0};
assert.equal(scope.canEditCheckoutNote(checkout),true);
let markup=scope.noteCellMarkup(checkout);
assert.ok(markup.includes('Edit note'),'Original checkout employee can edit');
assert.ok(!markup.includes('View changes'),'Unedited notes have no revision link');
scope.identity={employeeId:'OTHER_STAFF',isAdmin:false};
assert.equal(scope.canEditCheckoutNote(checkout),false);
assert.ok(!scope.noteCellMarkup(checkout).includes('data-edit-checkout-note'),'Coworker cannot see edit action');
scope.identity={employeeId:'MANAGER',isAdmin:true};
assert.equal(scope.canEditCheckoutNote(checkout),true);
assert.ok(scope.noteCellMarkup(checkout).includes('Edit note'),'Store admin can edit');
const checkin={...checkout,delta:1};
assert.equal(scope.canEditCheckoutNote(checkin),false);
assert.ok(!scope.noteCellMarkup(checkin).includes('Edit note'),'Cannot edit check-ins');
markup=scope.noteCellMarkup({...checkout,note:'<script>alert("x")</script>',noteRevision:2,noteUpdatedAt:'2026-11-21',noteUpdatedBy:'MANAGER'});
assert.ok(markup.includes('&lt;script&gt;'),'Note HTML is escaped');
assert.ok(!markup.includes('<script>'),'No raw script injection');
assert.ok(markup.includes('View changes'),'Edited notes show revision history');
assert.ok(markup.includes('Edited'),'Edited notes show attribution');
console.log('Checkout-note UI tests passed (author/admin permissions, check-ins, escaping, revisions).');
