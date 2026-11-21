/* Repair Queue UI. Stock changes happen only in atomic repair API transactions. */
(() => {
'use strict';
const STATUS={checked_in:'Checked In',in_progress:'In Progress',waiting_parts:'Waiting for Parts',ready:'Ready for Pickup',completed:'Completed',cancelled:'Cancelled'};
let rows=[],historical=false,loading=false,version=0,cameraTicketId=null;
const $r=id=>document.getElementById(id);
const safe=v=>esc(v??'');
const usd=v=>money(Number(v||0));
const key=()=>crypto.randomUUID();
const closed=t=>t.status==='completed'||t.status==='cancelled';
const canPickup=t=>!t.pickedUpAt&&(t.status==='completed'||t.status==='ready');
const displayStatus=t=>t.pickedUpAt?'Paid / Picked Up':t.status==='completed'?'Completed · Awaiting Pickup':status(t.status);
const status=v=>STATUS[v]||v;
function reset(){
 version++;overviewVersion++;rows=[];historical=false;cameraTicketId=null;loading=false;
 if($r('overviewRepairActive'))for(const id of ['overviewRepairActive','overviewRepairProgress','overviewRepairPickup','overviewRepairBalance'])$r(id).textContent='—';
 if($r('overviewRepairTickets'))$r('overviewRepairTickets').innerHTML='<div class="empty">Loading repairs…</div>';
 if($r('repairShowHistory'))$r('repairShowHistory').checked=false;
 if($r('repairSearch'))$r('repairSearch').value='';
 if($r('repairQueueList'))$r('repairQueueList').innerHTML='<div class="empty">Open the Repairs tab to load this location.</div>';
}
function renderList(){
 const el=$r('repairQueueList'),summary=$r('repairQueueSummary');
 if(!el||!summary)return;
 const search=$r('repairSearch').value.trim().toLowerCase();
 const list=rows.filter(r=>(r.id+' '+r.device+' '+r.repairType+' '+r.createdByName+' '+r.createdBy+' '+r.customerName+' '+r.serialNumber).toLowerCase().includes(search));
 summary.textContent=list.length+' '+(historical?'closed':'active')+' repair'+(list.length===1?'':'s')+(historical?' · most recently picked up / closed first':' · oldest check-in first · ready/completed repairs await pickup');
 el.innerHTML=list.length?list.map(r=>
  '<article class="repair-ticket">'+
   '<div class="repair-ticket-head"><div><small class="muted">Repair #'+Number(r.id)+' · '+safe(time(r.createdAt))+'</small>'+
   '<h3>'+safe(r.device)+'</h3><p class="repair-ticket-meta">'+safe(r.repairType)+(r.customerName?' · '+safe(r.customerName):'')+'</p></div>'+
   '<span class="repair-ticket-status">'+safe(displayStatus(r))+'</span></div>'+
   '<div class="repair-ticket-summary">'+
    '<div><small>Quoted</small><strong>'+usd(r.quotedTotal)+'</strong></div>'+
    '<div><small>Paid upfront</small><strong>'+usd(r.paidUpfront)+'</strong></div>'+
    '<div><small>Balance due</small><strong>'+usd(r.balanceDue)+'</strong></div>'+
    '<div><small>Parts used</small><strong>'+Number(r.partsUsed)+'</strong></div></div>'+
   '<div class="repair-ticket-footer"><div class="repair-ticket-meta">Checked in by <strong>'+safe(r.createdByName)+' ('+safe(r.createdBy)+')</strong><br>'+
    'Technician: '+safe(r.createdByName)+' ('+safe(r.createdBy)+')'+'</div>'+
    '<div style="display:flex;gap:8px;flex-wrap:wrap"><button type="button" class="btn" data-repair-open="'+Number(r.id)+'">Open ticket</button>'+
    (canPickup(r)?'<button type="button" class="btn primary" data-repair-pickup="'+Number(r.id)+'">Mark Paid / Picked Up</button>':'')+'</div></div></article>'
 ).join(''):'<div class="empty">'+(historical?'No paid/picked-up or cancelled repairs.':'No active repairs at this location.')+'</div>';
}
let overviewVersion=0;
function overviewRender(data){
 const active=$r('overviewRepairActive'),progress=$r('overviewRepairProgress'),pickup=$r('overviewRepairPickup'),
       balance=$r('overviewRepairBalance'),list=$r('overviewRepairTickets'),message=$r('overviewRepairMessage');
 if(!active||!progress||!pickup||!balance||!list||!message)return;
 active.textContent=String(data.length);
 progress.textContent=String(data.filter(r=>r.status==='in_progress').length);
 pickup.textContent=String(data.filter(r=>r.status==='ready'||r.status==='completed').length);
 balance.textContent=usd(data.reduce((sum,r)=>sum+Math.max(0,Number(r.balanceDue||0)),0));
 message.textContent=data.length?'Showing the first '+Math.min(4,data.length)+' repairs in check-in order.':'No active repairs at this store.';
 list.innerHTML=data.length?data.slice(0,4).map(r=>
  '<div class="overview-repair-entry"><div><small>Repair #'+Number(r.id)+' · '+safe(time(r.createdAt))+'</small>'+
  '<h3>'+safe(r.device)+' · '+safe(r.repairType)+'</h3>'+
  '<small>'+safe(displayStatus(r))+' · '+safe(r.createdByName||r.createdBy)+'</small></div>'+
  '<div class="overview-repair-entry-money"><strong>'+usd(r.balanceDue)+' due</strong>'+
  '<small>'+usd(r.quotedTotal)+' quoted · '+usd(r.paidUpfront)+' upfront</small></div></div>'
 ).join(''):'<div class="empty">No repairs in the active queue.</div>';
}
async function refreshOverview(){
 if(!identity)return;
 const stamp=++overviewVersion,store=identity.storeId;
 try{
  const data=await api('repairs?history=false');
  if(stamp!==overviewVersion||!identity||identity.storeId!==store)return;
  overviewRender(data);
 }catch(err){
  if(stamp!==overviewVersion||!identity||identity.storeId!==store)return;
  const el=$r('overviewRepairMessage');
  if(el)el.textContent='Unable to load repairs: '+err.message;
 }
}
async function refresh(){
 if(loading||!identity||!$r('view-repairs')?.classList.contains('active'))return;
 const stamp=++version,store=identity.storeId;loading=true;
 try{const next=await api('repairs?history='+historical);if(stamp!==version||store!==identity.storeId)return;rows=next;renderList();void refreshOverview();}
 catch(err){if(stamp===version){$r('repairQueueList').innerHTML='<div class="empty">'+safe(err.message)+'</div>';toast('Repair queue: '+err.message,true)}}
 finally{loading=false}
}
function field(name,value){return '<div><small class="muted">'+safe(name)+'</small><strong>'+safe(value||'—')+'</strong></div>'}
async function createTicket(){
 modal('Check in repair','<form id="repairCreateForm"><div class="form-grid">'+
  '<div><label for="repairDevice">Device *</label><input id="repairDevice" required maxlength="120" placeholder="iPhone 14 Pro"></div>'+
  '<div><label for="repairType">Repair type *</label><input id="repairType" required maxlength="150" placeholder="Soft OLED replacement"></div>'+
  '<div><label for="repairQuote">Total quoted ($) *</label><input id="repairQuote" type="number" min="0" max="99999999.99" step=".01" value="0.00" required></div>'+
  '<div><label for="repairUpfront">Paid up front ($) *</label><input id="repairUpfront" type="number" min="0" max="99999999.99" step=".01" value="0.00" required></div>'+
  '<div><label for="repairCustomer">Customer name</label><input id="repairCustomer" maxlength="120"></div>'+
  '<div><label for="repairContact">Customer contact</label><input id="repairContact" maxlength="150"></div>'+
  '<div><label for="repairSerial">Serial / IMEI</label><input id="repairSerial" maxlength="120"></div>'+
  '<div class="span2"><label for="repairNotes">Internal repair notes</label><textarea id="repairNotes" maxlength="2000" rows="3"></textarea></div></div>'+
  '<p class="repair-muted">The signed-in employee is automatically assigned as technician and recorded as the check-in employee. Payments are tracking only; no charge is processed.</p>'+
  '<div class="modal-actions"><button type="button" class="btn" id="repairCancelCreate">Cancel</button><button class="btn primary" id="repairSubmitCreate">Add to queue</button></div></form>');
 $r('repairCancelCreate').onclick=closeModal;
 $r('repairCreateForm').onsubmit=async e=>{
  e.preventDefault();const button=$r('repairSubmitCreate'),quote=Number($r('repairQuote').value),upfront=Number($r('repairUpfront').value);
  if(quote<0||upfront<0||upfront>quote){toast('Upfront payment cannot exceed the quote.',true);return}
  if(button.disabled)return;button.disabled=true;button.textContent='Saving…';
  const form=$r('repairCreateForm');form.dataset.requestKey||=key();
  try{
   const result=await api('repairs','POST',{
    device:$r('repairDevice').value.trim(),repairType:$r('repairType').value.trim(),
    quotedTotal:quote,paidUpfront:upfront,customerName:$r('repairCustomer').value.trim(),
    customerContact:$r('repairContact').value.trim(),serialNumber:$r('repairSerial').value.trim(),
    notes:$r('repairNotes').value.trim(),
    idempotencyKey:form.dataset.requestKey
   });
   closeModal();toast('Repair #'+result.id+' checked in');await refresh();await openTicket(result.id);
  }catch(err){toast('Repair check-in failed: '+err.message,true);button.disabled=false;button.textContent='Add to queue'}
 };
 $r('repairCreateForm').addEventListener('input',()=>delete $r('repairCreateForm').dataset.requestKey);
 $r('repairCreateForm').addEventListener('change',()=>delete $r('repairCreateForm').dataset.requestKey);
}
async function confirmPickup(id,balance,button){
 if(button.disabled)return;
 if(!window.confirm('Confirm that the remaining '+usd(balance)+' has been paid and the customer picked up the device? This action is final.'))return;
 button.disabled=true;
 const original=button.textContent;
 button.textContent='Confirming pickup…';
 button.dataset.requestKey||=key();
 try{
  const result=await api('repairs/'+id+'/pickup','POST',{idempotencyKey:button.dataset.requestKey});
  toast(result.duplicate?'Pickup already recorded.':'Repair paid and picked up');
  await refresh();
  if($r('modalRoot').querySelector('#repairConfirmPickup'))await openTicket(id);
 }catch(err){
  toast('Pickup not recorded: '+err.message,true);
  if(button.isConnected){button.disabled=false;button.textContent=original}
 }
}
async function openTicket(id,scanned=''){
 let detail;
 try{detail=await api('repairs/'+id)}
 catch(err){toast('Cannot open repair: '+err.message,true);return}
 const t=detail.ticket,lock=closed(t);
 const partLines=detail.parts.map(p=>'<div class="repair-detail-line"><div><strong>'+safe(p.name)+'</strong> · '+safe(p.sku)+
  '<br><small>×'+Number(p.quantity)+' · '+safe(p.scannedBy)+' · '+safe(time(p.usedAt))+
  (p.returnedAt?' · Returned '+safe(time(p.returnedAt)):'')+'</small></div>'+
  (p.returnedAt?'<span class="tag">Returned</span>':'<button class="btn" type="button" data-repair-return="'+Number(p.id)+'" data-qty="'+Number(p.quantity)+'">Return stock</button>')+'</div>').join('');
 const payments=detail.payments.map(p=>'<div>'+usd(p.amount)+' · '+safe(p.employeeId)+' · '+safe(time(p.createdAt))+'</div>').join('');
 const timeline=detail.events.map(ev=>'<div><strong>'+safe(ev.kind.replaceAll('_',' '))+'</strong> · '+safe(ev.employeeId)+' · '+safe(time(ev.createdAt))+
  '<br>'+safe(ev.details)+'</div>').join('');
 const statusOptions=Object.entries(STATUS).map(([value,label])=>'<option value="'+value+'"'+(value===t.status?' selected':'')+'>'+safe(label)+'</option>').join('');
 const stocked='<option value="">Select part instead of scanning…</option>'+items.filter(x=>x.quantity>0).map(x=>
  '<option value="'+safe(x.id)+'">'+safe(x.name)+' · '+safe(x.sku)+' ('+Number(x.quantity)+' in stock)</option>').join('');
 modal('Repair #'+id+' · '+t.device,
  '<p class="repair-ticket-meta">'+safe(t.repairType)+' · Checked in '+safe(time(t.createdAt))+'</p>'+
  '<div class="repair-detail-info" style="margin-top:13px">'+
   field('Checked in by',t.createdByName+' ('+t.createdBy+')')+field('Status',displayStatus(t))+
   field('Quoted',usd(t.quotedTotal))+field('Upfront',usd(t.paidUpfront))+
   field('Final payment',usd(t.additionalPaid))+field('Balance due',usd(t.balanceDue))+
   field('Customer',t.customerName)+field('Contact',t.customerContact)+
   field('Serial / IMEI',t.serialNumber)+field('Technician (checked in by)',t.createdByName+' ('+t.createdBy+')')+'</div>'+
  (t.notes?'<p class="repair-muted"><strong>Internal notes:</strong> '+safe(t.notes)+'</p>':'')+
  '<section class="repair-details-section"><h3>Repair status</h3>'+
   '<div class="repair-inline-control"><div><label for="repairStatusSelect">Status</label><select id="repairStatusSelect">'+statusOptions+'</select></div>'+
   '<button type="button" class="btn" id="repairStatusSave">Update status</button></div>'+
   '<p class="repair-muted">Ready for Pickup and Completed repairs remain in the queue until the balance is received and the device is picked up. Paid/picked-up tickets move to history.</p></section>'+
  '<section class="repair-details-section"><h3>Parts used</h3><div class="repair-detail-list">'+(partLines||'<p class="muted">No parts recorded yet.</p>')+'</div>'+
   '<form id="repairPartForm"><div class="repair-inline-control">'+
   '<div><label for="repairPartCode">Scan / enter SKU or barcode</label><input id="repairPartCode" maxlength="150" placeholder="Barcode or SKU" autocomplete="off" value="'+safe(scanned)+'"></div>'+
   '<button type="button" class="btn" id="repairCameraPart">▣ Camera scan</button></div>'+
   '<div class="repair-inline-control"><div><label for="repairPartItem">Or choose inventory part</label><select id="repairPartItem">'+stocked+'</select></div>'+
   '<div style="flex:0 1 100px"><label for="repairPartQty">Quantity</label><input id="repairPartQty" type="number" min="1" max="10000" step="1" value="1" required></div>'+
   '<button class="btn primary" id="repairAddPart"'+(lock?' disabled':'')+'>Deduct & add</button></div>'+
   '<p class="repair-muted">Each confirmed part deducts store stock and creates a linked checkout entry. Returns create a reversing stock movement.</p></form></section>'+
  '<section class="repair-details-section"><h3>Payment and pickup</h3>'+
   '<div class="repair-payments"><div>Deposit at check-in: '+usd(t.paidUpfront)+'</div>'+
   (payments||'<div>No final payment recorded yet.</div>')+
   '<div><strong>Balance to collect: '+usd(t.balanceDue)+'</strong></div></div>'+
   (t.pickedUpAt
     ?'<p class="repair-ticket-status">Paid / Picked Up</p><p class="repair-muted">Confirmed '+safe(time(t.pickedUpAt))+' by '+safe(t.pickedUpBy||'—')+'</p>'
     :canPickup(t)
      ?'<p class="repair-muted">Confirm that the customer has paid the remaining '+usd(t.balanceDue)+' and has collected the device. This action is final.</p>'+
       '<button type="button" class="btn primary" id="repairConfirmPickup">Mark Paid / Picked Up</button>'
      :'<p class="repair-muted">Available once the repair is marked Ready for Pickup or Completed. Only the initial deposit and the final remaining balance are recorded.</p>')+
   '<p class="repair-muted">For tracking only: Inventory Manager does not charge cards or process refunds.</p></section>'+
  '<section class="repair-details-section"><h3>Activity history</h3><div class="repair-events">'+(timeline||'<p class="muted">No activity yet.</p>')+'</div></section>');
 $r('repairStatusSave').onclick=async()=>{
  const button=$r('repairStatusSave');if(button.disabled)return;button.disabled=true;
  try{await api('repairs/'+id+'/status','PUT',{status:$r('repairStatusSelect').value});toast('Repair status saved');await refresh();await openTicket(id)}
  catch(err){toast(err.message,true);button.disabled=false}
 };
 const partForm=$r('repairPartForm');
 partForm.addEventListener('input',()=>delete partForm.dataset.requestKey);
 partForm.addEventListener('change',()=>delete partForm.dataset.requestKey);
 partForm.onsubmit=async e=>{
  e.preventDefault();const btn=$r('repairAddPart'),code=$r('repairPartCode').value.trim(),itemId=$r('repairPartItem').value;
  const quantity=Number($r('repairPartQty').value);
  if(btn.disabled||lock)return;
  if(!code&&!itemId){toast('Scan a barcode or choose a part.',true);return}
  if(!Number.isSafeInteger(quantity)||quantity<1||quantity>10000){toast('Enter a valid quantity.',true);return}
  btn.disabled=true;btn.textContent='Deducting…';
  partForm.dataset.requestKey||=key();
  try{
   const result=await api('repairs/'+id+'/parts','POST',{
    itemId:code?null:itemId,code:code||null,quantity,idempotencyKey:partForm.dataset.requestKey
   });
   toast(result.duplicate?'Already recorded — no further deduction.':'Part deducted from inventory');
   await load();await refresh();await openTicket(id);
  }catch(err){toast('Unable to record part: '+err.message,true);btn.disabled=false;btn.textContent='Deduct & add'}
 };
 $r('repairCameraPart').onclick=()=>{
  if(!navigator.mediaDevices?.getUserMedia){toast('Camera requires HTTPS and camera permission. Enter the code manually.',true);return}
  cameraTicketId=id;$r('cameraBtn').click();
 };
 if(canPickup(t)){
  const button=$r('repairConfirmPickup');
  button.onclick=()=>void confirmPickup(id,t.balanceDue,button);
 }
 $r('modalRoot').querySelectorAll('[data-repair-return]').forEach(button=>{
  button.onclick=async()=>{
   if(button.disabled)return;
   const part=Number(button.dataset.repairReturn),qty=Number(button.dataset.qty);
   if(!window.confirm('Return '+qty+' unit(s) of this part to inventory? This creates a reversal movement.'))return;
   button.disabled=true;button.dataset.requestKey||=key();
   try{await api('repairs/'+id+'/parts/'+part+'/return','POST',{idempotencyKey:button.dataset.requestKey});
    toast('Part returned to stock');await load();await refresh();await openTicket(id);
   }catch(err){toast(err.message,true);button.disabled=false}
  };
 });
}
function handleCameraScan(code){
 if(cameraTicketId===null)return false;
 const id=cameraTicketId;cameraTicketId=null;
 void openTicket(id,String(code||'').trim());
 return true;
}
const modalRoot=$r('modalRoot');
if(modalRoot)new MutationObserver(()=>{
 if(cameraTicketId!==null&&!modalRoot.querySelector('.scanner-preview'))cameraTicketId=null;
}).observe(modalRoot,{childList:true});
$r('repairNewBtn').onclick=()=>void createTicket();
$r('repairSearch').oninput=renderList;
$r('repairShowHistory').onchange=()=>{historical=$r('repairShowHistory').checked;rows=[];renderList();void refresh()};
$r('repairQueueList').onclick=e=>{
 const pickup=e.target.closest('[data-repair-pickup]');
 if(pickup){
  const row=rows.find(r=>Number(r.id)===Number(pickup.dataset.repairPickup));
  if(row&&canPickup(row))
   void confirmPickup(Number(row.id),row.balanceDue,pickup);
  return;
 }
 const btn=e.target.closest('[data-repair-open]');
 if(btn)void openTicket(Number(btn.dataset.repairOpen));
};
window.RepairQueue={open:refresh,refresh,refreshOverview,reset,handleCameraScan};
})();
