# garage-watch-platform

Novo repositório da fundação T01 da Garagem IA. Não contém nem substitui o protótipo T00a em `projects/garage-watch`.

## Estado inicial

O projeto começa como fundação executável de serviço .NET 10, com limites de domínio/aplicação/infraestrutura/host e contratos versionados. Banco, cache, broker, captura, IA e UI somente serão ativados conforme decisão e necessidade documentadas; nenhuma capacidade física é presumida.

## Desenvolvimento

Use Docker Compose para compilar, executar e testar. Copie `.env.example` para `.env` e troque `AUTH_PASSWORD` por uma senha local exclusiva (mínimo 6 caracteres) antes de iniciar. Esse mínimo reduz a segurança; mantenha o host em loopback e não reutilize a senha. `AUTH_ROLE` seleciona `Administrator` ou `Viewer`; não há cadastro de usuários. Depois execute:

```bash
docker compose build
docker compose run --rm api dotnet restore GarageWatch.slnx --locked-mode
docker compose --profile test run --rm test
docker compose --profile rtsp-contract config --quiet
bash scripts/verify-rtsp-contract.sh
docker compose up --build
```

O serviço fica somente em `127.0.0.1:${APP_PORT}`. O painel em `/` usa cookie HttpOnly/SameSite Strict; login e sessão são locais/dev-only, sem banco, e cookies são invalidados ao reiniciar o host. `/health/live` permanece mínimo e público no loopback; `/health/ready`, câmeras e playlist/segmentos live exigem sessão. O gateway fornece HLS dos segmentos recentes do spool T02 sem copiar mídia para outro armazenamento. O painel usa Bootstrap 5.3.8 CSS auto-hospedado e HLS.js 1.7.3 local (sem CDN), envia cookie same-origin nas requisições HLS, usa buffer limitado e marca descontinuidades entre epochs; depois do login também apresenta liveness da API, readiness/captura, adaptador/capacidades, versão do host, contagem/cota de segmentos retidos, lacunas e estado do gateway/player. IA, NAS, banco e broker aparecem como não configurados; CPU/RAM/disco livre de host não são inferidos. HLS nativo permanece fallback. Presença de clientes é contagem aproximada de abas autenticadas por heartbeat/lease efêmero (TTL 30 s, limite 64), sem identificar pessoas; o player mantém áudio e controles nativos e oferece “Ir ao vivo” sem autoplay. Compatibilidade e latência CT02 ainda não foram validadas em dispositivos reais. A configuração de papel vale para uma conta local, não é gestão de identidade multiusuário nem autorização de produção. T01-O adiciona métricas e traces OpenTelemetry; envio ao coletor é opcional via `OTEL_EXPORTER_OTLP_ENDPOINT` em `.env`. T02 usa captura sintética por padrão; `verify-rtsp-contract.sh` testa FFmpeg com MediaMTX/H.264 e AAC sintéticos, sem validar câmera/hardware. O perfil opcional `camera-integration` consome vídeo H.264 e áudio AAC do main stream pelo Neolink via rede Docker externa interna `garage-watch-rtsp-lab`, com spool local e painel ainda loopback; veja `docs/capture.md`. Os lockfiles `packages.lock.json` são travados. Para parar, `docker compose down` (dados locais ficam em `./data`). Leia também `docs/architecture.md`, `docs/configuration.md`, `docs/capture.md`, `docs/dependencies.md` e `contracts/`. Nunca use este repositório para armazenar vídeo pessoal ou segredos.

## Licença

Este repositório é público para consulta, mas ainda não possui licença de reutilização definida. Não presuma permissão para reutilizar o código; as dependências de terceiros mantêm suas licenças próprias.
