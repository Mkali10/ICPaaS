const $=s=>document.querySelector(s);
async function load(){
  const [capRes,engRes]=await Promise.all([fetch('/api/v1/system/capabilities'),fetch('/api/v1/telephony/engines')]);
  const cap=await capRes.json(),engines=await engRes.json();
  $('#profile').textContent=cap.deploymentProfile;
  $('#engines').textContent=String(engines.filter(x=>x.availability!=='Disabled').length);
  const db=cap.capabilities.find(x=>x.key==='database');$('#database').textContent=db?.state||'unknown';
  const media=cap.capabilities.filter(x=>x.key==='turn'||x.key==='rtpengine').some(x=>x.state==='bundled-available');$('#media').textContent=media?'Available':'External';
  $('#engine-list').innerHTML=engines.map(e=>`<div class="engine"><div><strong>${e.engineKey}</strong><span>${e.message}</span></div><b class="badge ${e.availability.toLowerCase()}">${e.availability}</b></div>`).join('');
}
$('#refresh').addEventListener('click',load);load().catch(()=>{$('#engine-list').innerHTML='<div class="engine"><div><strong>API unavailable</strong><span>Start the control plane to inspect capabilities.</span></div><b class="badge">offline</b></div>'});
