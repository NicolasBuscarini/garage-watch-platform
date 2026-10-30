# Runbook local — iniciar plataforma e live

Use Docker Compose; o host deve ter Docker e Compose. O perfil permanece dev-only e vinculado a loopback.

## Inicialização sintética

```bash
cp .env.example .env
chmod 600 .env
mkdir -p data
```

Edite `.env`: defina `APP_UID`/`APP_GID` com `id -u`/`id -g`, usuário inicial e senha exclusiva (`AUTH_PASSWORD`, mínimo 6 caracteres; somente números é permitido). Essa política de conveniência vale apenas para loopback e reduz a resistência a adivinhação; não reutilize senha nem publique o serviço na LAN. Login tem rate limit e lockout, mas OTP/e-mail não está implementado e não é presumido como substituto. Mantenha `CAPTURE_PROFILE=synthetic` para primeiro smoke. Credenciais bootstrap só valem quando `data/identity/garagewatch.db` está vazio; alterar o arquivo depois não redefine senha. Não versionar `.env` nem `data/`.

```bash
docker compose build
docker compose run --rm api dotnet restore GarageWatch.slnx --locked-mode
docker compose --profile test run --rm test
docker compose config --quiet
docker compose up -d --build api
curl --fail --silent http://127.0.0.1:8081/health/live
```

Abra `http://127.0.0.1:8081`, entre e use **Ir ao vivo**/Play. API e painel não ficam disponíveis na LAN. `/health/ready` é protegido e retorna 503 quando captura não confirmou segmento; liveness continua independente.

## Laboratório RTSP (opcional)

Inicie Neolink conforme suas instruções locais e conecte-o à rede Docker externa interna `garage-watch-rtsp-lab`. A câmera inventariada é declarada como Reolink E1 básica; RTSP standalone não foi confirmado e pode exigir Home Hub/NVR. Não presuma compatibilidade direta: confira [`docs/operacao/04_HARDWARE.md`](../../docs/operacao/04_HARDWARE.md) e valide o bridge/stream disponível antes. A configuração de segredo do bridge fica fora do Git. Confirme a rede com `docker network inspect garage-watch-rtsp-lab --format '{{.Internal}}'` (deve ser `true`); se o nome já existir como não interna, pare em vez de removê-la. No `.env`, configure a URI de alias, sem segredo, por exemplo `rtsp://neolink:8554/garage/mainStream`.

Não execute o perfil sintético e o RTSP como consumidores simultâneos:

```bash
docker compose stop api
docker compose --profile camera-integration config --quiet
docker compose --profile camera-integration up -d --build api-camera-integration
curl --fail --silent http://127.0.0.1:8084/health/live
```

Abra `http://127.0.0.1:8084` e autentique-se. Na queda da fonte, o watchdog usa `CAPTURE_SEGMENT_TIMEOUT_SECONDS` (0 = `max(15, 3 × CAPTURE_SEGMENT_SECONDS + 5)`; 20 s com segmento de 5 s), registra gap e reconecta com backoff de 1–30 s. O painel exibe aviso no quadro e, após persistir novo epoch, recria HLS e tenta retomar o live. Browser pode exigir Play manual. Ajuste timeout maior se os intervalos legítimos de segmento/keyframe excederem o padrão.

Para interromper sem apagar mídia local:

```bash
docker compose --profile camera-integration stop api-camera-integration
docker compose stop api
```

Não use `down --volumes` nem remova `data/` para parar o serviço. Para reprodução de falhas sem câmera, `bash scripts/verify-rtsp-contract.sh` usa MediaMTX/publisher sintéticos; isso não homologa a câmera real, rede ou operação prolongada.

Procedimento de contexto e limites adicionais: [guia compartilhado de execução local](../../docs/operacao/16_GUIA_EXECUCAO_LOCAL.md).

## Frontend React em HMR (opt-in)

O caminho padrão segue a UI estática compilada na imagem da API. Para desenvolver com Vite, clone/atualize o submodule e inicie `docker compose --profile web-dev up -d api web-dev`; acesse `http://127.0.0.1:5173`. Vite faz proxy same-origin para API/health e o cookie continua HttpOnly. Só publique no loopback. Não inicie esse profile enquanto `api-camera-integration`/outro consumidor estiver usando o mesmo banco ou spool; a API sintética também abre captura e compartilha `./data`. O serviço Vite é dev-only e não aparece no perfil de produção.
