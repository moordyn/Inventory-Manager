/* Repair Queue UI. Stock changes happen only in atomic repair API transactions. */
(() => {
'use strict';
const STATUS={checked_in:'Checked In',in_progress:'In Progress',waiting_parts:'Waiting for Parts',ready:'Ready for Pickup',completed:'Completed',cancelled:'Cancelled'};
let rows=[],historical=false,loading=false,version=0,technicians=[],cameraTicketId=null;
const $r=id=>document.getElementById(id);
const safe=v=>esc(v??'');
const usd=v=>money(Number(v||0));
const key=()=>crypto.randomUUID();
const closed=t=>t.status==='completed'||t.status==='cancelled';
const status=v=>STATUS[v]||v;
function reset(){
 version++;rows=[];historical=false;technicians=[];cameraTicketId=null;loading=false;
 if($r('repairShowHistory'))$r('repairShowHistory').checked=false;
 if($r('repairSearch'))$r('repairSearch').value='';
 if($r('repairQueueList'))$r('repairQueueList').innerHTML='<div class="empty">Open the Repairs tab to load this location.</div>';
}
async function techniciansList(){technicians=await api('repairs/technicians');return technicians;}
function techOptions(selected){
 return '<option value="">Unassigned</option>'+technicians.map(t=>'<option value="'+safe(t.id)+'"'+(t.id===selected?' selected':'')+'>'+safe(t.name)+' ('+safe(t.id)+')</option>').join('');
}
function renderList(){
 const el=$r('repairQueueList'),summary=$r('repairQueueSummary');
 if(!el||!summary)return;
 const search=$r('repairSearch').value.trim().toLowerCase();
 const list=rows.filter(r=>(r.id+' '+r.device+' '+r.repairType+' '+r.createdByName+' '+r.createdBy+' '+r.customerName+' '+r.serialNumber).toLowerCase().includes(search));
 summary.textContent=list.length+' '+(historical?'closed':'active')+' repair'+(list.length===1?'':'s')+(historical?' · newest closed first':' · oldest check-in first');
 el.innerHTML=list.length?list.map(r=>
  '<article class="repair-ticket">'+
   '<div class="repair-ticket-head"><div><small class="muted">Repair #'+Number(r.id)+' · '+safe(time(r.createdAt))+'</small>'+
   '<h3>'+safe(r.device)+'</h3><p class="repair-ticket-meta">'+safe(r.repairType)+(r.customerName?' · '+safe(r.customerName):'')+'</p></div>'+
   '<span class="repair-ticket-status">'+safe(status(r.status))+'</span></div>'+
   '<div class="repair-ticket-summary">'+
    '<div><small>Quoted</small><strong>'+usd(r.quotedTotal)+'</strong></div>'+
    '<div><small>Paid upfront</small><strong>'+usd(r.paidUpfront)+'</strong></div>'+
    '<div><small>Balance due</small><strong>'+usd(r.balanceDue)+'</strong></div>'+
    '<div><small>Parts used</small><strong>'+Number(r.partsUsed)+'</strong></div></div>'+
   '<div class="repair-ticket-footer"><div class="repair-ticket-meta">Checked in by <strong>'+safe(r.createdByName)+' ('+safe(r.createdBy)+')</strong><br>'+
    (r.assignedTo?'Technician: '+safe(r.assignedTo):'Unassigned')+'</div>'+
    '<button type="button" class="btn" data-repair-open="'+Number(r.id)+'">Open ticket</button></div></article>'
 ).join(''):'<div class="empty">'+(historical?'No completed or cancelled repairs.':'No active repairs at this location.')+'</div>';
}
async function refresh(){
 if(loading||!identity||!$r('view-repairs')?.classList.contains('active'))return;
 const stamp=++version,store=identity.storeId;loading=true;
 try{const next=await api('repairs?history='+historical);if(stamp!==version||store!==identity.storeId)return;rows=next;renderList();}
 catch(err){if(stamp===version){$r('repairQueueList').innerHTML='<div class="empty">'+safe(err.message)+'</div>';toast('Repair queue: '+err.message,true)}}
 finally{loading=false}
}
function field(name,value){return '<div><small class="muted">'+safe(name)+'</small><strong>'+safe(value||'—')+'</strong></div>'}
async function createTicket(){
 try{await techniciansList()}catch(err){toast('Could not load technicians: '+err.message,true);return}
 modal('Check in repair','<form id="repairCreateForm"><div class="form-grid">'+
  '<div><label for="repairDevice">Device *</label><input id="repairDevice" required maxlength="120" placeholder="iPhone 14 Pro"></div>'+
  '<div><label for="repairType">Repair type *</label><input id="repairType" required maxlength="150" placeholder="Soft OLED replacement"></div>'+
  '<div><label for="repairQuote">Total quoted ($) *</label><input id="repairQuote" type="number" min="0" max="99999999.99" step=".01" value="0.00" required></div>'+
  '<div><label for="repairUpfront">Paid up front ($) *</label><input id="repairUpfront" type="number" min="0" max="99999999.99" step=".01" value="0.00" required></div>'+
  '<div><label for="repairCustomer">Customer name</label><input id="repairCustomer" maxlength="120"></div>'+
  '<div><label for="repairContact">Customer contact</label><input id="repairContact" maxlength="150"></div>'+
  '<div><label for="repairSerial">Serial / IMEI</label><input id="repairSerial" maxlength="120"></div>'+
  '<div><label for="repairAssignee">Assigned technician</label><select id="repairAssignee">'+techOptions('')+'</select></div>'+
  '<div class="span2"><label for="repairNotes">Internal repair notes</label><textarea id="repairNotes" maxlength="2000" rows="3"></textarea></div></div>'+
  '<p class="repair-muted">Check-in employee and time are recorded automatically. Money entered here is for tracking only; no charge is processed.</p>'+
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
    notes:$r('repairNotes').value.trim(),assignedTo:$r('repairAssignee').value||null,
    idempotencyKey:form.dataset.requestKey
   });
   closeModal();toast('Repair #'+result.id+' checked in');await refresh();await openTicket(result.id);
  }catch(err){toast('Repair check-in failed: '+err.message,true);button.disabled=false;button.textContent='Add to queue'}
 };
 $r('repairCreateForm').addEventListener('input',()=>delete $r('repairCreateForm').dataset.requestKey);
 $r('repairCreateForm').addEventListener('change',()=>delete $r('repairCreateForm').dataset.requestKey);
}
async function openTicket(id,scanned=''){
 let detail;
 try{[detail]=await Promise.all([api('repairs/'+id),techniciansList()])}
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
   field('Checked in by',t.createdByName+' ('+t.createdBy+')')+field('Status',status(t.status))+
   field('Quoted',usd(t.quotedTotal))+field('Upfront',usd(t.paidUpfront))+
   field('Additional paid',usd(t.additionalPaid))+field('Balance due',usd(t.balanceDue))+
   field('Customer',t.customerName)+field('Contact',t.customerContact)+
   field('Serial / IMEI',t.serialNumber)+field('Technician',t.assignedTo)+'</div>'+
  (t.notes?'<p class="repair-muted"><strong>Internal notes:</strong> '+safe(t.notes)+'</p>':'')+
  '<section class="repair-details-section"><h3>Status & technician</h3>'+
   '<div class="repair-inline-control"><div><label for="repairStatusSelect">Status</label><select id="repairStatusSelect">'+statusOptions+'</select></div>'+
   '<button type="button" class="btn" id="repairStatusSave">Update status</button></div>'+
   '<div class="repair-inline-control"><div><label for="repairTechnicianSelect">Technician</label><select id="repairTechnicianSelect">'+techOptions(t.assignedTo)+'</select></div>'+
   '<button type="button" class="btn" id="repairAssigneeSave">Assign</button></div>'+
   '<p class="repair-muted">Completed and cancelled repairs move to history. Reopen a ticket before adding parts.</p></section>'+
  '<section class="repair-details-section"><h3>Parts used</h3><div class="repair-detail-list">'+(partLines||'<p class="muted">No parts recorded yet.</p>')+'</div>'+
   '<form id="repairPartForm"><div class="repair-inline-control">'+
   '<div><label for="repairPartCode">Scan / enter SKU or barcode</label><input id="repairPartCode" maxlength="150" placeholder="Barcode or SKU" autocomplete="off" value="'+safe(scanned)+'"></div>'+
   '<button type="button" class="btn" id="repairCameraPart">▣ Camera scan</button></div>'+
   '<div class="repair-inline-control"><div><label for="repairPartItem">Or choose inventory part</label><select id="repairPartItem">'+stocked+'</select></div>'+
   '<div style="flex:0 1 100px"><label for="repairPartQty">Quantity</label><input id="repairPartQty" type="number" min="1" max="10000" step="1" value="1" required></div>'+
   '<button class="btn primary" id="repairAddPart"'+(lock?' disabled':'')+'>Deduct & add</button></div>'+
   '<p class="repair-muted">Each confirmed part deducts store stock and creates a linked checkout entry. Returns create a reversing stock movement.</p></form></section>'+
  '<section class="repair-details-section"><h3>Payments</h3><div class="repair-payments"><div>Upfront: '+usd(t.paidUpfront)+'</div>'+
   (payments||'<div>No additional payments yet.</div>')+'</div>'+
   '<form id="repairPaymentForm" class="repair-inline-control"><div><label for="repairPaymentAmount">Additional payment ($)</label>'+
   '<input id="repairPaymentAmount" type="number" min=".01" max="'+Number(t.balanceDue)+'" step=".01" placeholder="0.00" required></div>'+
   '<button class="btn" id="repairPaymentSave"'+(Number(t.balanceDue)<=0||t.status==='cancelled'?' disabled':'')+'>Record payment</button></form>'+
   '<p class="repair-muted">Tracking only; no money is charged or refunded by this app.</p></section>'+
  '<section class="repair-details-section"><h3>Activity history</h3><div class="repair-events">'+(timeline||'<p class="muted">No activity yet.</p>')+'</div></section>');
 $r('repairStatusSave').onclick=async()=>{
  const button=$r('repairStatusSave');if(button.disabled)return;button.disabled=true;
  try{await api('repairs/'+id+'/status','PUT',{status:$r('repairStatusSelect').value});toast('Repair status saved');await refresh();await openTicket(id)}
  catch(err){toast(err.message,true);button.disabled=false}
 };
 $r('repairAssigneeSave').onclick=async()=>{
  const button=$r('repairAssigneeSave');if(button.disabled)return;button.disabled=true;
  try{await api('repairs/'+id+'/assign','PUT',{employeeId:$r('repairTechnicianSelect').value||null});toast('Technician saved');await refresh();await openTicket(id)}
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
 const paymentForm=$r('repairPaymentForm');
 paymentForm.addEventListener('input',()=>delete paymentForm.dataset.requestKey);
 paymentForm.onsubmit=async e=>{
  e.preventDefault();const button=$r('repairPaymentSave'),amount=Number($r('repairPaymentAmount').value);
  if(button.disabled)return;
  if(!Number.isFinite(amount)||amount<=0||amount>Number(t.balanceDue)){toast('Invalid payment amount.',true);return}
  button.disabled=true;paymentForm.dataset.requestKey||=key();
  try{
   const result=await api('repairs/'+id+'/payments','POST',{amount,idempotencyKey:paymentForm.dataset.requestKey});
   toast(result.duplicate?'Payment already recorded.':'Payment recorded');await refresh();await openTicket(id);
  }catch(err){toast(err.message,true);button.disabled=false}
 };
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
 const btn=e.target.closest('[data-repair-open]');
 if(btn)void openTicket(Number(btn.dataset.repairOpen));
};
window.RepairQueue={open:refresh,refresh,reset,handleCameraScan};
})();
