# garage-watch-platform

Novo repositório da fundação T01 da Garagem IA. Não contém nem substitui o protótipo T00a em `projects/garage-watch`.

## Estado inicial

O projeto começa como fundação executável de serviço .NET 10, com limites de domínio/aplicação/infraestrutura/host e contratos versionados. Banco, cache, broker, captura, IA e UI somente serão ativados conforme decisão e necessidade documentadas; nenhuma capacidade física é presumida.

## Desenvolvimento

Use Docker Compose para compilar, executar e testar. Copie `.env.example` para `.env` e substitua `AUTH_PASSWORD` por senha exclusiva com ao menos 6 caracteres antes do primeiro start; senha somente numérica é aceita. Essa conveniência reduz a resistência a adivinhação: mantenha o serviço em loopback, não reutilize senha e não exponha na LAN. Login preserva rate limit/lockout, mas isso é mitigação parcial; OTP/e-mail não está implementado. A senha bootstrap é usada uma vez para criar o administrador inicial e armazenada somente como hash ASP.NET Core Identity. `AUTH_USERNAME`/`AUTH_EMAIL` também são dados de bootstrap; depois da primeira conta, a fonte de verdade é o banco local e remover/alterar essas variáveis não troca a senha/usuário. O exemplo é deliberadamente inválido. T03-REG habilita cadastro same-origin local como User sem e-mail/verificação/recuperação e sem grants; UI de administração permanece pendente e cadastro não pode ser exposto na LAN. Captura RTSP detecta ausência prolongada de segmentos, tenta reconectar e mostra aviso sobre o player; comportamento e timeout são documentados em `docs/capture.md`, sem homologar recuperação de câmera física. Depois execute:

Passo a passo de inicialização, perfil sintético, laboratório RTSP, aviso de câmera offline e parada segura: [`docs/runbook.md`](docs/runbook.md).

```bash
docker compose build
docker compose run --rm api dotnet restore GarageWatch.slnx --locked-mode
docker compose --profile test run --rm test
docker compose --profile rtsp-contract config --quiet
bash scripts/verify-rtsp-contract.sh
docker compose up --build
```

O serviço fica somente em `127.0.0.1:${APP_PORT}`. T03-IAM adiciona SQLite em `/data/identity/garagewatch.db`, migrations EF Core e ASP.NET Core Identity; diretório e banco são restritos ao usuário do serviço. Em banco vazio, bootstrap exige `AUTH_USERNAME` e senha exclusiva com mínimo de 6 caracteres, sem exigência de composição e aceitando somente números, cria somente o primeiro Administrator e registra auditoria; se já existem contas, essas variáveis não reseedam nem alteram credenciais. Papéis `User`, `Viewer` e `Administrator`, grants por câmera e auditoria mínima são persistidos. T03-REG cria `User` local ativo sem e-mail/verificação/recuperação e sem grant, com tela de landing sem câmera; gestão administrativa de usuários/grants e pedidos de acesso ainda são tarefas posteriores. O endpoint de cadastro exige Origin same-origin e limita três tentativas por minuto no processo; não é seguro para LAN/produção. O painel em `/` usa cookie HttpOnly/SameSite Strict de 8 h, sem sliding; carimbo de segurança é revalidado no máximo a cada 15 s e mudanças IAM o atualizam. Reiniciar ainda invalida cookies pela chave Data Protection efêmera. `/health/live` permanece mínimo e público no loopback; `/health/ready` e perfil operacional exigem papel Administrator; câmera e playlist/segmentos live exigem sessão Viewer ou Administrator. O gateway fornece HLS dos segmentos recentes do spool T02 sem copiar mídia para outro armazenamento. O painel usa Bootstrap 5.3.8 CSS auto-hospedado e HLS.js 1.7.3 local (sem CDN), envia cookie same-origin nas requisições HLS, usa buffer limitado e marca descontinuidades entre epochs; depois do login também apresenta liveness da API, readiness/captura, adaptador/capacidades, versão do host, contagem/cota de segmentos retidos, lacunas e estado do gateway/player. IA, NAS, banco e broker aparecem como não configurados; CPU/RAM/disco livre de host não são inferidos. HLS nativo permanece fallback. Presença de clientes é contagem aproximada de abas autenticadas por heartbeat/lease efêmero (TTL 30 s, limite 64), sem identificar pessoas; o player usa controles próprios (play/pause, salto ±10 s, linha do tempo, volume/mudo, tela cheia e “Ir ao vivo”), sem autoplay ou controles nativos. Compatibilidade e latência CT02 ainda não foram validadas em dispositivos reais. A configuração de papel vale para uma conta local, não é gestão de identidade multiusuário nem autorização de produção. T01-O adiciona métricas e traces OpenTelemetry; envio ao coletor é opcional via `OTEL_EXPORTER_OTLP_ENDPOINT` em `.env`. T02 usa captura sintética por padrão; `verify-rtsp-contract.sh` testa FFmpeg com MediaMTX/H.264 e AAC sintéticos, sem validar câmera/hardware. O perfil opcional `camera-integration` consome vídeo H.264 e áudio AAC do main stream pelo Neolink via rede Docker externa interna `garage-watch-rtsp-lab`, com spool local e painel ainda loopback; veja `docs/capture.md`. Os lockfiles `packages.lock.json` são travados. Para parar, `docker compose down` (dados locais ficam em `./data`). Leia também `docs/architecture.md`, `docs/configuration.md`, `docs/capture.md`, `docs/dependencies.md` e `contracts/`. Nunca use este repositório para armazenar vídeo pessoal ou segredos.

## Frontend React independente

O código da UI e suas dependências estão em [`web/`](web/), submodule do repositório público [`garage-watch-web`](https://github.com/NicolasBuscarini/garage-watch-web), fixado no gitlink deste projeto. Clone com `git clone --recurse-submodules` ou execute `git submodule update --init --recursive`. O build da imagem usa Node `24.22.2` fixado por digest e copia apenas o Vite `dist/` para `/app/web`; ASP.NET Core continua servindo UI/API/HLS same-origin. Para HMR local, inicie `docker compose --profile web-dev up -d api web-dev` e abra `http://127.0.0.1:5173`; não habilite junto ao profile de captura se os dois serviços compartilharem spool/banco local. Validação do frontend isolado está no README do submodule. A UI oferece temas Bosque, Grafite, Oceano e Claro; a preferência é local ao navegador, não configuração de usuário.

## Licença

Este repositório é público para consulta, mas ainda não possui licença de reutilização definida. Não presuma permissão para reutilizar o código; as dependências de terceiros mantêm suas licenças próprias.
