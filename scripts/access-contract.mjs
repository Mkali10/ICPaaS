import fs from 'node:fs';

const read=path=>fs.readFileSync(path,'utf8');
const app=read('src/IcpaaS.Api/wwwroot/app.js');
const nav=read('src/IcpaaS.Api/wwwroot/index.html');
const supervisor=read('src/IcpaaS.Api/wwwroot/supervisor.js');
const middleware=read('src/IcpaaS.Api/EntitlementAccess.cs');
const program=read('src/IcpaaS.Api/Program.cs');
const exceptionHandler=read('src/IcpaaS.Api/ApiExceptionHandler.cs');

const requireContract=(condition,message)=>{if(!condition)throw new Error(message)};
const navViews=new Set([...nav.matchAll(/data-view="([a-z_]+)"/g)].map(x=>x[1]));
const actionViews=new Set([...app.matchAll(/data-go-view="([a-z_]+)"/g)].map(x=>x[1]));
const scripts=['app.js','infrastructure-console.js','contact-center.js','supervisor.js','recordings.js','entitlements.js'].map(x=>read(`src/IcpaaS.Api/wwwroot/${x}`)).join('\n');
const buttonIds=new Set([...scripts.matchAll(/<button[^>]*id=["']([^"']+)["']/g)].map(x=>x[1]).filter(x=>!x.includes('${')));

for(const view of actionViews)requireContract(navViews.has(view),`Action targets unknown view: ${view}`);
for(const id of buttonIds)requireContract(scripts.includes(`#${id}`),`Button has no bound handler: ${id}`);
for(const view of navViews){
 if(view==='monitor')requireContract(supervisor.includes("view!=='monitor'"),'Monitor navigation has no renderer');
 else requireContract(app.includes(`view==='${view}'`),`Navigation has no renderer: ${view}`);
}

requireContract(app.includes("if(platform)allowed=['overview','tenants','billing','guide'"),'Platform Setup Guide access missing');
requireContract(!app.match(/else if\(tenantAdmin\)allowed=\[[^\]]*'guide'/),'Tenant admin must not receive Setup Guide');
requireContract(middleware.includes('context.Request.Method==HttpMethods.Get?"recordings":"agent_desk"'),'Calls must separate history access from call-control access');
requireContract(supervisor.includes("(me?.entitlements||[]).includes('supervision')"),'Supervisor menu must require entitlement');
requireContract(!supervisor.includes('allowed=platform||'),'Platform must not enter a tenant supervisor view without tenant context');
requireContract(program.includes('ApiExceptionHandler.Write'),'API exception mapping must be installed');
for(const status of ['Status403Forbidden','Status400BadRequest','Status409Conflict','Status500InternalServerError'])requireContract(exceptionHandler.includes(status),`Missing exception response mapping: ${status}`);

console.log(`Access contract OK: ${navViews.size} navigation views, ${actionViews.size} static actions and ${buttonIds.size} button handlers checked.`);
