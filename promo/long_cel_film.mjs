// ClickZen long-form cel film. Seeded ink and paper primitives reused from the short film.
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { PAL,createPaperLayers,drawCelPolygon,makeRoundRectPts,drawBrushStroke,makeQuadPts,drawCelStar,drawCelPhone,drawCursorSpirit,drawAndroidBuddy,drawBoilingHeroLockup,drawCurvedSmear,drawCelLightning,drawCelSmokePuff,boilVariant,stepFrame } from './cel_film_engine.mjs';
const require=createRequire(import.meta.url);
const BUILD_DIR=process.env.CZ_BUILD_DIR || 'promo/.render';
fs.mkdirSync(BUILD_DIR,{recursive:true});
const {createCanvas}=require('@napi-rs/canvas');
const W=1920,H=1080,FPS=24,TOTAL=2736;
const chapters=[
 {start:0,voice:10,end:278,num:'01',title:'告别机械狂点',sub:'让繁琐操作，变得轻松', lines:['还在被机械连点器折磨？','模拟器端口每次重启都在变，','脚本一切横屏就彻底罢工？','别折腾了，试试完全重写的 ClickZen 2.1！']},
 {start:288,voice:298,end:603,num:'02',title:'原生重写，解压即用',sub:'C#  /  WinUI 3  /  Windows',lines:['2.0 起采用 C# 与 WinUI 3 原生重写，','界面清爽流畅；','内置 adb、scrcpy 4.1 与 FFmpeg，','解压即用，零环境配置！']},
 {start:613,voice:623,end:949,num:'03',title:'投屏 · 录制 · 回放',sub:'所见即所得的触控操作',lines:['应用内集成 scrcpy 毫秒级高清投屏，','支持多指触控与丝滑滑动；','按下 F9 直接在画面上录制完整手势轨迹，','随时原速或倍速精准回放。']},
 {start:959,voice:969,end:1305,num:'04',title:'模拟器，一键连接',sub:'自动发现实例与真实端口',lines:['模拟器助手，自动检测 MuMu、雷电、蓝叠等主流模拟器，','直接从配置文件读取真实动态端口；','支持一键绑定窗口，','后台或被遮挡也能捕获画面。']},
 {start:1315,voice:1325,end:1669,num:'05',title:'让画面成为条件',sub:'OpenCV 视觉自动化',lines:['图像识别自动化采用方案、任务、规则三级编排，','融合 OpenCV 模板匹配、颜色取点与变量表达式；','每个任务独立记忆横竖屏方向，','画面旋转也能精准命中！']},
 {start:1679,voice:1689,end:1988,num:'06',title:'多设备，协同工作',sub:'独立运行  /  变量同步',lines:['多台真机、无线设备与模拟器可同时独立运行方案，','更能通过内置 TCP 变量同步服务与加密令牌，','轻松实现跨设备、跨程序的实时协同。']},
 {start:1998,voice:2008,end:2450,num:'07',title:'一句话，交给 AI',sub:'MCP  /  33 个工具  /  按组授权',lines:['重磅升级：内置 MCP 服务器！','无论是 Claude、Cursor 还是 VS Code，','只需复制配置或通过 stdio 启动，','就能调用六大分组共 33 项工具，','带权限开关与调用审计，','让 AI 一句话帮你看画面、点屏幕、跑方案！']},
 {start:2460,voice:2470,end:2682,num:'08',title:'让自动化更从容',sub:'开源、免费，欢迎体验',lines:['ClickZen 2.1，现已在 GitHub 免费开源！','快去下载体验，','别忘了点赞、投币、收藏一键三连支持一下吧！']}
];
const canvas=createCanvas(W,H),ctx=canvas.getContext('2d'),paper=createPaperLayers();
function panel(x,y,w,h,fill,v,seed=1){drawCelPolygon(ctx,makeRoundRectPts(x,y,w,h,28,4),{fill,stroke:PAL.ink,strokeWidth:6,regOffset:[5,4],boilAmp:1.5,seed,variant:v});}
function text(t,x,y,size=36,color=PAL.ink,align='left'){ctx.fillStyle=color;ctx.textAlign=align;ctx.textBaseline='middle';ctx.font=`${size}px SarasaBold`;ctx.fillText(t,x,y);}
function tag(t,x,y,v,fill=PAL.marigold){ctx.font='27px SarasaBold';let w=ctx.measureText(t).width+44;panel(x,y,w,50,fill,v,42);text(t,x+22,y+26,27);}
function arrow(x,y,X,Y,v,color=PAL.ink){drawBrushStroke(ctx,makeQuadPts([x,y],[(x+X)/2,(y+Y)/2-35],[X,Y],14),{width:7,color,boilAmp:1.7,seed:80,variant:v}); drawCelStar(ctx,X,Y,13,0,color,90,v);}
function render(f){
 const i=chapters.findLastIndex(c=>f>=c.start),c=chapters[Math.max(0,i)],v=boilVariant(f,2),q=stepFrame(f,2),local=f-c.start,t=local/24;
 ctx.drawImage(paper.baseCanvas,0,0);
 const intro=Math.min(1,local/14), xoff=(1-intro)*-180;
 // Persistent chapter header and living page ornaments
 ctx.save(); ctx.translate(xoff,0);
 text('CLICKZEN 2.1   /   动画介绍',100,95,25,PAL.ink); text(c.num,1810,100,60,PAL.tomato,'right');
 drawBrushStroke(ctx,[[100,130],[1810,130]],{width:4,seed:15,variant:v,boilAmp:1});
 text(c.title,125,231,74); text(c.sub,129,306,34,PAL.emerald);
 ctx.restore();
 // Staggered scene entrances and slow, held-frame camera breathing.
 ctx.save(); const appear=Math.min(1,local/18); const zoom=0.97+0.03*appear+0.012*Math.sin(q*.018);
 ctx.translate(960,580);ctx.scale(zoom,zoom);ctx.translate(-960,-580);ctx.translate(0,(1-appear)*85);
 let sway=Math.sin(q*.08)*12;
 if(i===0){
   drawCelPhone(ctx,1020+sway,575,{w:280,h:460,stressed:true,screenFill:PAL.tomato,variant:v});
   drawCursorSpirit(ctx,1395,620,{scale:1.5,stride:q*.12,variant:v});
   tag('端口 : ???',530,460,v);tag('重复点击 × 999',570,680,v,PAL.cream);
   for(let j=0;j<5;j++)drawCelStar(ctx,840+j*95,345+Math.sin(q*.1+j)*23,22,.2,PAL.marigold,100+j,v);
 } else if(i===1){
   panel(570,390,780,310,PAL.cream,v,112);panel(610,430,260,240,PAL.emerald,v,113);
   text('C#',740,550,82,PAL.cream,'center');text('WinUI 3',935,470,48);text('ADB  +  scrcpy',935,545,33);text('FFmpeg',935,610,33);
   drawCursorSpirit(ctx,1455,630,{scale:1.1,stride:q*.1,variant:v});tag('解压即用',1160,735,v);
 } else if(i===2){
   drawCelPhone(ctx,715,550+sway,{w:240,h:380,screenFill:PAL.emerald,variant:v});
   panel(1000,375,570,330,PAL.cream,v,125);panel(1030,405,510,210,PAL.emerald,v,126);
   text('投屏画面',1280,510,48,PAL.cream,'center'); text('● REC     ━━━━━━━     ▶',1030,665,33,PAL.tomato);
   arrow(850,510,985,510,v,PAL.marigold);tag('F9 录制',535,765,v);
 } else if(i===3){
   ['MuMu','雷电','蓝叠'].forEach((n,j)=>{let x=510+j*380; panel(x,420+Math.sin(q*.06+j)*8,310,260,[PAL.emerald,PAL.marigold,PAL.cream][j],v,140+j);text(n,x+155,485,47,PAL.ink,'center');text('ADB  :  自动发现',x+155,565,27,PAL.ink,'center');text('✓  连接 / 绑定',x+155,625,25,PAL.ink,'center');});
   tag('后台窗口捕获',1030,745,v); drawCursorSpirit(ctx,1625,730,{scale:.85,stride:q*.12,variant:v});
 } else if(i===4){
   ['方案','任务','规则'].forEach((n,j)=>{let x=480+j*290;panel(x,390,230,115,[PAL.emerald,PAL.marigold,PAL.cream][j],v,150+j);text(n,x+115,448,42,PAL.ink,'center');if(j<2)arrow(x+240,445,x+278,445,v);});
   panel(580,565,760,170,PAL.cream,v,160); text('模板匹配  ·  颜色  ·  变量',960,620,41,PAL.ink,'center');text('✓  按任务记忆画面方向',960,680,32,PAL.emerald,'center');
   drawCelStar(ctx,1420,560,75,.2,PAL.marigold,161,v);
 } else if(i===5){
   [0,1,2].forEach(j=>{drawCelPhone(ctx,620+j*340,545+Math.sin(q*.08+j)*10,{w:175,h:300,screenFill:[PAL.emerald,PAL.marigold,PAL.tomato][j],variant:v,seed:200+j*50});if(j<2)arrow(735+j*340,550,850+j*340,550,v);});
   tag('独立运行',520,755,v);tag('变量同步 · TCP',990,755,v,PAL.cream);
 } else if(i===6){
   panel(715,390,510,270,PAL.marigold,v,170);text('AI  +  MCP',970,470,65,PAL.ink,'center');text('33 个工具',970,560,42,PAL.ink,'center');
   [0,1,2].forEach(j=>{let x=490+j*480;drawCelLightning(ctx,970,670,x,765,24,PAL.emerald,210+j,v);drawAndroidBuddy(ctx,x,785,{scale:.7,variant:v,seed:700+j*10});});
   tag('截图 / 操作 / 自动化',1200,320,v,PAL.cream);
 } else {
   drawBoilingHeroLockup(ctx,960,480,.85,v,Math.floor(q/90)%2===0,Math.sin(q*.1)*5);
   text('github.com/Exmeaning/ClickZen',960,685,37,PAL.ink,'center');
   tag('点赞  ·  投币  ·  收藏',735,750,v,PAL.marigold);
 }
 ctx.restore();
 // Bottom subtitle: proportional segments are approximate against narration.
 if(f>=c.voice && f<c.end){let u=(f-c.voice)/(c.end-c.voice);let idx=Math.min(c.lines.length-1,Math.floor(u*c.lines.length));let line=c.lines[idx];
   panel(240,915,1440,80,PAL.ink,v,300);text(line,960,955,36,PAL.cream,'center');}
 // Element-carried entrance: 8-frame swipe/smoke around each chapter boundary.
 if(i>0 && local<10){let p=local/10;drawCurvedSmear(ctx,[-250+2000*p,720],[600+2000*p,200],[1000+2000*p,420],65,12,PAL.emerald,400,v,true);drawCelSmokePuff(ctx,400+1350*p,520,105,p,410,v);}
 ctx.save();ctx.globalCompositeOperation='multiply';ctx.drawImage(paper.grainCanvases[boilVariant(f,3,2)],0,0);ctx.restore();
}
async function main(){
 if(process.argv[2]==='stills'){fs.mkdirSync(`${BUILD_DIR}/long_stills`,{recursive:true});for(let i=0;i<8;i++){let f=chapters[i].start+80;render(f);fs.writeFileSync(`${BUILD_DIR}/long_stills/${i+1}.png`,canvas.toBuffer('image/png'));}return;}
 let bin=process.env.FFMPEG_BIN||'ffmpeg',out=process.env.LONG_VIDEO||'promo/ClickZen-Cel-Long.mp4',audio=process.env.LONG_AUDIO||`${BUILD_DIR}/long_master.wav`;
 let ff=spawn(bin,['-y','-loglevel','warning','-f','rawvideo','-pix_fmt','rgba','-s','1920x1080','-r','24','-i','pipe:0','-i',audio,'-frames:v',String(TOTAL),'-c:v','libx264','-preset','veryfast','-crf','19','-pix_fmt','yuv420p','-c:a','aac','-b:a','192k','-ar','48000','-movflags','+faststart',out],{stdio:['pipe','inherit','inherit']});
 ff.on('error',e=>{console.error(e);process.exit(1)});
 for(let f=0;f<TOTAL;f++){if(f%2===0)render(f); if(!ff.stdin.write(canvas.data()))await new Promise(r=>ff.stdin.once('drain',r));if(f%240===0)console.log(`frame ${f}/${TOTAL}`);}
 ff.stdin.end();await new Promise((r,j)=>ff.on('close',code=>code===0?r():j(Error(`ffmpeg exit ${code}`))));console.log('Done',out);
}
main().catch(e=>{console.error(e);process.exit(1)});
