# garage-watch-platform

Novo repositório da fundação T01 da Garagem IA. Não contém nem substitui o protótipo T00a em `projects/garage-watch`.

## Estado inicial

O projeto começa como fundação executável de serviço .NET 10, com limites de domínio/aplicação/infraestrutura/host e contratos versionados. Banco, cache, broker, captura, IA e UI somente serão ativados conforme decisão e necessidade documentadas; nenhuma capacidade física é presumida.

## Desenvolvimento

Use Docker Compose para compilar, executar e testar. Copie `.env.example` para `.env` e substitua `AUTH_PASSWORD` por senha exclusiva com ao menos 12 caracteres antes do primeiro start; ela é usada uma única vez para criar o administrador inicial e armazenada somente como hash ASP.NET Core Identity. `AUTH_USERNAME`/`AUTH_EMAIL` também são dados de bootstrap; depois da primeira conta, a fonte de verdade é o banco local e remover/alterar essas variáveis não troca a senha/usuário. O exemplo é deliberadamente inválido. Mantenha o serviço em loopback. O cadastro público e a UI de administração ainda não foram implementados. Depois execute:

```bash
docker compose build
docker compose run --rm api dotnet restore GarageWatch.slnx --locked-mode
docker compose --profile test run --rm test
docker compose --profile rtsp-contract config --quiet
bash scripts/verify-rtsp-contract.sh
docker compose up --build
```

O serviço fica somente em `127.0.0.1:${APP_PORT}`. T03-IAM adiciona SQLite em `/data/identity/garagewatch.db`, migrations EF Core e ASP.NET Core Identity; diretório e banco são restritos ao usuário do serviço. Em banco vazio, bootstrap exige `AUTH_USERNAME` e senha de pelo menos 12 caracteres, cria somente o primeiro Administrator e registra auditoria; se já existem contas, essas variáveis não reseedam nem alteram credenciais. Papéis `User`, `Viewer` e `Administrator`, grants por câmera e auditoria mínima são persistidos, mas cadastro e gestão de usuários/grants na UI e endpoints administrativos ainda são tarefas posteriores. O painel em `/` usa cookie HttpOnly/SameSite Strict de 8 h, sem sliding; carimbo de segurança é revalidado no máximo a cada 15 s e mudanças IAM o atualizam. Reiniciar ainda invalida cookies pela chave Data Protection efêmera. `/health/live` permanece mínimo e público no loopback; `/health/ready` e perfil operacional exigem papel Administrator; câmera e playlist/segmentos live exigem sessão Viewer ou Administrator. O gateway fornece HLS dos segmentos recentes do spool T02 sem copiar mídia para outro armazenamento. O painel usa Bootstrap 5.3.8 CSS auto-hospedado e HLS.js 1.7.3 local (sem CDN), envia cookie same-origin nas requisições HLS, usa buffer limitado e marca descontinuidades entre epochs; depois do login também apresenta liveness da API, readiness/captura, adaptador/capacidades, versão do host, contagem/cota de segmentos retidos, lacunas e estado do gateway/player. IA, NAS, banco e broker aparecem como não configurados; CPU/RAM/disco livre de host não são inferidos. HLS nativo permanece fallback. Presença de clientes é contagem aproximada de abas autenticadas por heartbeat/lease efêmero (TTL 30 s, limite 64), sem identificar pessoas; o player usa controles próprios (play/pause, salto ±10 s, linha do tempo, volume/mudo, tela cheia e “Ir ao vivo”), sem autoplay ou controles nativos. Compatibilidade e latência CT02 ainda não foram validadas em dispositivos reais. A configuração de papel vale para uma conta local, não é gestão de identidade multiusuário nem autorização de produção. T01-O adiciona métricas e traces OpenTelemetry; envio ao coletor é opcional via `OTEL_EXPORTER_OTLP_ENDPOINT` em `.env`. T02 usa captura sintética por padrão; `verify-rtsp-contract.sh` testa FFmpeg com MediaMTX/H.264 e AAC sintéticos, sem validar câmera/hardware. O perfil opcional `camera-integration` consome vídeo H.264 e áudio AAC do main stream pelo Neolink via rede Docker externa interna `garage-watch-rtsp-lab`, com spool local e painel ainda loopback; veja `docs/capture.md`. Os lockfiles `packages.lock.json` são travados. Para parar, `docker compose down` (dados locais ficam em `./data`). Leia também `docs/architecture.md`, `docs/configuration.md`, `docs/capture.md`, `docs/dependencies.md` e `contracts/`. Nunca use este repositório para armazenar vídeo pessoal ou segredos.

## Licença

Este repositório é público para consulta, mas ainda não possui licença de reutilização definida. Não presuma permissão para reutilizar o código; as dependências de terceiros mantêm suas licenças próprias.
