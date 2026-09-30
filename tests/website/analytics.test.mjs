// Dependency-free checks of the consent gate. Live network validation is separate.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const source = readFileSync('website/public/analytics.js','utf8');
function run(choice, hostname='findra-search.netlify.app') {
  const added=[]; const commands=[];
  const element=()=>({setAttribute(){},addEventListener(){},querySelector(){return {focus(){}}},dataset:{}});
  const document={head:{appendChild:x=>added.push(x)},body:{appendChild:x=>added.push(x)},createElement:element,querySelectorAll:()=>[],referrer:'',cookie:''};
  const window={addEventListener(){}};
  const ctx={window,document,location:{hostname,origin:`https://${hostname}`,pathname:'/',search:''},localStorage:{getItem:()=>choice,setItem(){}},URL,URLSearchParams,Set,MutationObserver:class {observe(){}},gtag:(...args)=>commands.push(args),clarity:(...args)=>commands.push(args)};
  vm.runInNewContext(source,ctx);
  return {scripts:added.filter(x=>x.src).map(x=>x.src),banner:added.find(x=>x.className==='findra-consent'),commands};
}
for(const choice of [null,'denied','invalid']) {
 const result=run(choice);assert.deepEqual(result.scripts,[]);assert.equal(result.commands.length,0);
 assert.equal(result.banner.hidden,choice==='denied');
}
const granted=run('granted');
assert.deepEqual(granted.scripts,['https://www.googletagmanager.com/gtag/js?id=G-909686JLLS','https://www.clarity.ms/tag/yqly8fd2cl']);
assert.equal(granted.banner.hidden,true);
const config=granted.commands.find(x=>x[0]==='config');
assert.equal(config[2].allow_google_signals,false);assert.equal(config[2].allow_ad_personalization_signals,false);
assert.equal(granted.commands.find(x=>x[0]==='consent')[2].ad_storage,'denied');
assert.deepEqual(run('granted','example.com').scripts,[]);
console.log('Consent gate: unknown, rejected, invalid, granted and hostname checks passed.');
