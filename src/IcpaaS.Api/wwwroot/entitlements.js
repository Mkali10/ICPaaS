(()=>{
 const modules=[
  ['campaigns','Campaign setup','Queues, processes, lead data, campaigns and rechurn'],
  ['agent_desk','Agent Desk','Browser phone, assigned leads and dispositions'],
  ['supervision','Live supervision','Monitor, whisper, barge and agent states'],
  ['recordings','Calls and recordings','Call history, playback and downloads'],
  ['team','Team and permissions','Create users and assign workspace roles'],
  ['integrations','Integrations','Webhooks and connected applications'],
  ['quality','Quality and compliance','Reviews, scorecards and compliance controls'],
  ['reports','Reports','Calling, campaign, agent and outcome reports'],
  ['audit','Audit trail','Workspace security and configuration events'],
  ['infrastructure','SIP infrastructure','SIP servers and trunks'],
  ['numbers','Phone numbers','DID and outbound CLI inventory'],
  ['routing','Call routes','Inbound, outbound and failover routing'],
  ['operations','System status','Workspace runtime and automation status']
 ];
 const originalFetch=window.fetch.bind(window);
 window.fetch=async(input,init={})=>{
  const url=String(input?.url||input);
  if(init.method==='PATCH'&&url.includes('/api/v1/platform/tenants/')&&document.querySelector('#service-entitlements')){
   try{const body=JSON.parse(init.body||'{}');body.serviceEntitlements=[...document.querySelectorAll('#service-entitlements input:checked')].map(x=>x.value);init={...init,body:JSON.stringify(body)}}catch{}
  }
  const response=await originalFetch(input,init);
  if((!init.method||init.method==='GET')&&/\/api\/v1\/platform\/tenants\/[0-9a-f-]+$/i.test(url)&&response.ok){try{const body=await response.clone().json();window.currentTenantEntitlements=body.service_entitlements||[]}catch{}}
  return response;
 };
 const pruneUnauthorizedActions=()=>{
  const permitted=new Set([...document.querySelectorAll('#nav button:not([hidden])')].map(x=>x.dataset.view));
  document.querySelectorAll('#root [data-go-view]').forEach(button=>{if(!permitted.has(button.dataset.goView))button.hidden=true});
 };
 const render=()=>{
  pruneUnauthorizedActions();
  const form=document.querySelector('#limits-form');
  if(!form||form.querySelector('#service-entitlements'))return;
  const selected=new Set(window.currentTenantEntitlements||[]),fieldset=document.createElement('fieldset');
  fieldset.id='service-entitlements';fieldset.className='wide entitlement-grid';
  fieldset.innerHTML=`<legend>Customer module access</legend><p>Select exactly what this customer may see and call. Unselected modules are blocked in both the menu and API.</p>${modules.map(([value,label,help])=>`<label class="entitlement-option"><input type="checkbox" value="${value}" ${selected.has(value)?'checked':''}><span><strong>${label}</strong><small>${help}</small></span></label>`).join('')}<p class="entitlement-warning">Changes apply when customer users sign in again.</p>`;
  form.querySelector('button.primary').before(fieldset);
 };
 new MutationObserver(render).observe(document.querySelector('#root'),{childList:true,subtree:true});
})();
