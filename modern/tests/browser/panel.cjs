const {chromium}=require('playwright-core');
const fs=require('fs');const nodePath=require('path');
(async()=>{
const browser=await chromium.launch({headless:true,...(process.env.RUNPAD_CHROME?{executablePath:process.env.RUNPAD_CHROME}:{channel:'chrome'}),args:['--no-sandbox']});
const page=await browser.newPage({viewport:{width:1600,height:1000}});
const devices=Array.from({length:30},(_,i)=>({id:'pc'+i,name:'PC'+String(i+1).padStart(2,'0'),classroom:i<20?'Кабинет 43':'Кабинет 44',user:'student',userSeenAt:new Date().toISOString(),modules:[]}));
let submissions=[];let errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.route('http://runpad.test/**',async route=>{const path=new URL(route.request().url()).pathname;if(path==='/')return route.fulfill({contentType:'text/html',body:fs.readFileSync(nodePath.join(__dirname,'../../src/Runpad.Server/wwwroot/index.html'),'utf8')});if(path==='/api/admin/devices')return route.fulfill({json:devices});if(path==='/api/admin/commands'){if(route.request().method()==='POST')submissions.push(route.request().postDataJSON());return route.fulfill({json:[]})}return route.fulfill({status:404});});
await page.goto('http://runpad.test/');await page.fill('#token','test-token');await page.click('#connect');await page.waitForSelector('.card');
if(await page.locator('.card').count()!==30)throw Error('Expected 30 cards');
await page.click('#all');await page.selectOption('#classroom','Кабинет 43');
if(await page.locator('.card').count()!==20)throw Error('Classroom filter failed');
page.once('dialog',d=>d.accept('https://example.com'));await page.click('[data-command="open-url"]');await page.waitForTimeout(250);
if(submissions.length!==1||submissions[0].deviceIds.length!==20)throw Error('Bulk command target set incorrect');
await page.selectOption('#classroom','');const last=await page.locator('.card').last().boundingBox();if(last.y+last.height>1000)throw Error('30 screen cards do not fit the viewport');
if(errors.length)throw Error(errors.join('\n'));console.log('PASS: 30 screen cards, classroom filter, selection and bulk command targeting, no JavaScript errors');await browser.close();
})().catch(e=>{console.error(e);process.exit(1)});
