const $=id=>document.getElementById(id);
let hls;
let refreshTimer;
async function api(path,options={}){return fetch(path,{...options,credentials:'same-origin',headers:{'Content-Type':'application/json',...(options.headers||{})}})}
function show(id,visible){$(id).classList.toggle('hidden',!visible)}
function stopRefresh(){if(refreshTimer){clearInterval(refreshTimer);refreshTimer=undefined}}
function startRefresh(){stopRefresh();refreshTimer=setInterval(()=>refresh().catch(()=>{}),3000)}
function attachPlayer(url){
  if(hls){hls.destroy();hls=undefined}
  const video=$('live');video.removeAttribute('src');video.load();
  if(window.Hls&&Hls.isSupported()){
    hls=new Hls({lowLatencyMode:false,maxBufferLength:12,backBufferLength:6,maxMaxBufferLength:18,
      xhrSetup:xhr=>{xhr.withCredentials=true}});
    hls.loadSource(url);hls.attachMedia(video);
    hls.on(Hls.Events.MANIFEST_PARSED,()=>{$('live-message').textContent='Clique em reproduzir para iniciar áudio e vídeo.'});
    video.addEventListener('playing',()=>{$('live-message').textContent='Reprodução HLS ativa.'});
    hls.on(Hls.Events.ERROR,(_,data)=>{if(data.fatal){const safe=[data.type,data.details,Number.isInteger(data.response?.code)&&data.response.code>0?`HTTP ${data.response.code}`:''].filter(Boolean).join(' / ');$('live-message').textContent=`Falha HLS (${safe||'causa desconhecida'}).`;if(data.type===Hls.ErrorTypes.NETWORK_ERROR)hls.startLoad();else hls.recoverMediaError()}});
    $('live-message').textContent='Reprodução HLS local; autenticação por sessão.';
  }else if(video.canPlayType('application/vnd.apple.mpegurl')){
    video.src=url;$('live-message').textContent='Clique em reproduzir para iniciar áudio e vídeo.';
  }else $('live-message').textContent='Este navegador não oferece reprodução HLS/MSE compatível.';
}
async function refresh(){
  const session=await api('/api/v1/auth/session');
  if(!session.ok)throw new Error('Não foi possível consultar a sessão local.');
  if(!(await session.json()).authenticated){stopRefresh();show('login',true);show('dashboard',false);show('logout',false);return false}
  const response=await api('/api/v1/cameras');
  if(response.status===401){stopRefresh();show('login',true);show('dashboard',false);show('logout',false);return false}
  if(!response.ok)throw new Error('Não foi possível consultar o estado local.');
  const [camera]=await response.json();
  show('login',false);show('dashboard',true);show('logout',true);
  $('camera-name').textContent=camera.cameraId;$('state').textContent=camera.status;
  $('state').className='badge '+(camera.status==='capturing'?'ok':'bad');$('adapter').textContent=camera.adapter;
  $('segments').textContent=camera.segments;$('gaps').textContent=camera.gaps;
  const video=$('live');if(video.dataset.url!==camera.liveUrl){video.dataset.url=camera.liveUrl;attachPlayer(camera.liveUrl)}
  return true;
}
$('login-form').addEventListener('submit',async event=>{event.preventDefault();const formElement=event.currentTarget;const submit=formElement.querySelector('button[type="submit"]');$('login-error').textContent='';submit.disabled=true;
  try{const form=new FormData(formElement);const response=await api('/api/v1/auth/login',{method:'POST',body:JSON.stringify({username:form.get('username'),password:form.get('password')})});
    if(!response.ok){$('login-error').textContent='Acesso não autorizado. Confira usuário e senha.';return}formElement.reset();if(await refresh())startRefresh()
  }catch{$('login-error').textContent='Não foi possível acessar o serviço local. Tente novamente.'}
  finally{submit.disabled=false}});
$('logout').addEventListener('click',async()=>{stopRefresh();await api('/api/v1/auth/logout',{method:'POST'});if(hls){hls.destroy();hls=undefined}$('live').removeAttribute('src');$('live').load();show('dashboard',false);show('logout',false);show('login',true)});
refresh().then(authenticated=>{if(authenticated)startRefresh()}).catch(error=>{$('message').textContent=error.message});
