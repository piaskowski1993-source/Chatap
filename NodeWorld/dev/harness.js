// Runs the page's script in Node with fake browser objects and checks the main behaviours.
// Make dev/cw.js first: sed -n "/<script>/,/<\/script>/p" index.html | sed "1d;$d" > dev/cw.js
const fs=require('fs');
const noop=()=>{};
const ctxProxy=new Proxy({}, {get:(t,k)=>{ if(k==='measureText') return s=>({width:s.length*7}); if(k==='createLinearGradient'||k==='createRadialGradient') return ()=>({addColorStop:noop}); if(k in t) return t[k]; return noop;}, set:(t,k,v)=>{t[k]=v;return true;}});
// fake Web Audio: every node/param is a callable proxy
let audioCalls=0;
function anyProxy(){ const f=function(){ audioCalls++; return anyProxy(); }; return new Proxy(f,{get:(t,k)=>{ if(k==='currentTime') return T/1000; if(k==='sampleRate') return 8000; if(k==='state') return 'running'; if(k==='getChannelData') return ()=>new Float32Array(16000); if(k in t && k!=='length' && k!=='name') return t[k]; return anyProxy(); }, set:(t,k,v)=>{t[k]=v; return true;}}); }
function el(){ return {style:{},classList:{toggle:noop},children:[],hidden:true,_html:'',set innerHTML(v){this._html=v;this.children=[]},get innerHTML(){return this._html},textContent:'',append(...c){this.children.push(...c)},querySelectorAll(){return this.children.filter(c=>c.tag==='button')},addEventListener:noop,setPointerCapture:noop,focus:noop,value:'',getContext:()=>ctxProxy,getBoundingClientRect:()=>({left:0,top:0,width:1400,height:800}),parentElement:null,scrollHeight:0}; }
const els={}; global.document={documentElement:{},addEventListener:()=>{},getElementById:id=>els[id]||(els[id]=Object.assign(el(),{parentElement:el()})),createElement:tag=>Object.assign(el(),{tag}),fonts:null};
const listeners={}; global.addEventListener=(k,f)=>{(listeners[k]=listeners[k]||[]).push(f)}; global.ResizeObserver=class{observe(){}}; let T=0; global.performance={now:()=>T};
let rafCb=null; global.requestAnimationFrame=cb=>{rafCb=cb}; global.window={devicePixelRatio:1, AudioContext: function(){ return anyProxy(); }}; global.location={hash:''};
global.setInterval=(f)=>{ global.__musicTick=f; };
eval(fs.readFileSync(__dirname+'/cw.js','utf8')+`;globalThis.__t={robot,interact,termSpot,doorRect,P,BOARD,ST_INST,factory,know,doorOpen,dlg,closeDialog,quiz,progress,wantedTrack,get scene(){return scene},get swarm(){return swarm},blocked,nearTerminal,nearDoor,defense,initAudio,get voices(){return voices}};`);
const tick=n=>{for(let i=0;i<n;i++){T+=1000/60; rafCb(T); if(global.__musicTick) global.__musicTick();}};
const opts=()=>els.dlgOpts.children.map(c=>c.textContent||c.tag);
const talkUntilOptions=()=>{ for(let i=0;i<60*40 && !t.dlg.optionsShown;i++) tick(1); };
let t;
(async()=>{ await Promise.resolve(); await Promise.resolve();
 t=__t; t.initAudio(); console.log('audio started');
 for (const id in t.P){ const sp=t.termSpot(t.P[id]); t.robot.x=sp.x; t.robot.y=sp.y; if(t.blocked(sp.x,sp.y)||t.nearTerminal()!==t.P[id]) console.log('BAD world terminal',id); }
 // door closed at start
 const d=t.doorRect(t.P['A.log']); t.robot.x=d.x+d.w/2; t.robot.y=d.y+d.h+30; t.interact();
 console.log('closed door -> scene', t.scene.kind, '| thought:', t.robot.thought.text);
 // ask the LogWriter to open
 const sp=t.termSpot(t.P['A.log']); t.robot.x=sp.x; t.robot.y=sp.y; t.interact(); talkUntilOptions();
 console.log('LogWriter options:', opts());
 // password: a wrong one, then the easter egg
 const typePw=v=>{ els.dlgOpts.children.find(c=>c.textContent&&c.textContent.includes('Input the password')).onclick(); const row=els.dlgOpts.children[0]; row.children[0].value=v; row.children[1].onclick(); talkUntilOptions(); };
 typePw('nope'); console.log('wrong password -> door open?', !!t.doorOpen['A.log'], '| last line:', els.dlgLine.textContent);
 typePw(' AdminAdmin1 '); console.log('adminadmin1 -> door open A/B?', t.doorOpen['A.log'], t.doorOpen['B.log'], '| last line:', els.dlgLine.textContent);
 console.log('options after unlock:', opts());
 // quiz: wrong on questions 2 and 7, then all right
 const takeQuiz=wrongAt=>{ for(let i=0;i<10;i++){ const right=t.quiz.items[i].opts.findIndex(o=>o.right); const pick=wrongAt.includes(i+1)?(right+1)%4:right; els.quizOpts.children[pick].onclick(); } return els.quizText.children.map(c=>c.innerHTML).join(' / '); };
 els.dlgOpts.children.find(c=>c.textContent&&c.textContent.includes('Take the quiz')).onclick();
 for(let i=0;i<60*20&&t.quiz.phase!=='ask';i++) tick(1);
 console.log('quiz open, track:', t.wantedTrack(), '| first question:', els.quizText.textContent);
 console.log('fail result:', takeQuiz([2,7]), '| track:', t.wantedTrack());
 els.quizOpts.children[0].onclick();   // try again
 console.log('pass result:', takeQuiz([]), '| passed:', [...t.progress.passed], '| track:', t.wantedTrack());
 els.quizOpts.children[0].onclick(); talkUntilOptions();   // close
 console.log('after quiz, machine says:', els.dlgLine.textContent); t.closeDialog();
 t.robot.x=d.x+d.w/2; t.robot.y=d.y+d.h+30; t.interact(); console.log('enter -> scene', t.scene.kind, t.scene.node);
 // every station terminal reachable
 for (const k in t.ST_INST.log.A){ const inst=t.ST_INST.log.A[k]; const s=t.termSpot(inst); t.robot.x=s.x; t.robot.y=s.y; if(t.blocked(s.x,s.y)||t.nearTerminal()!==inst) console.log('BAD station terminal',k, t.blocked(s.x,s.y), t.nearTerminal()&&t.nearTerminal().id); }
 console.log('station terminals checked');
 const arm=t.ST_INST.log.A.arm, as=t.termSpot(arm); t.robot.x=as.x; t.robot.y=as.y; t.interact();
 tick(30); console.log('connecting text:', els.dlgLine.textContent);
 talkUntilOptions(); console.log('arm speaker/line:', els.dlgSpeaker.textContent, '|', els.dlgLine.textContent); console.log('arm options:', opts());
 els.dlgOpts.children[0].onclick(); talkUntilOptions(); console.log('after ask, last line:', els.dlgLine.textContent); t.closeDialog();
 // dynamic answer: node.txt last line
 tick(60*30);
 const nf=t.ST_INST.log.A.nodeFile, ns=t.termSpot(nf); t.robot.x=ns.x; t.robot.y=ns.y; t.interact(); talkUntilOptions();
 els.dlgOpts.children.find(c=>c.textContent&&c.textContent.includes('last line')).onclick(); talkUntilOptions(); console.log('node.txt last line answer:', els.dlgLine.textContent); t.closeDialog();
 // leave and stand near a busy wire to hear packets
 t.robot.x=130; t.robot.y=1360; t.interact(); const B=t.BOARD.A; t.robot.x=B.x+1450; t.robot.y=B.y+1500;
 let maxVoices=0; for(let i=0;i<60*60;i++){ tick(1); maxVoices=Math.max(maxVoices,t.voices.size); }
 console.log('scene', t.scene.kind, '| max packet voices at once:', maxVoices, '| audio calls:', audioCalls>1000?'>1000':audioCalls);
 tick(60*120); console.log('2 more min OK');
 tick(60*90); console.log('victory over, track:', t.wantedTrack());
})().catch(e=>{console.error('ERR',e);process.exit(1)});
