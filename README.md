# garage-watch-platform

Novo repositório da fundação T01 da Garagem IA. Não contém nem substitui o protótipo T00a em `projects/garage-watch`.

## Estado inicial

O projeto começa como fundação executável de serviço .NET 10, com limites de domínio/aplicação/infraestrutura/host e contratos versionados. Banco, cache, broker, captura, IA e UI somente serão ativados conforme decisão e necessidade documentadas; nenhuma capacidade física é presumida.

## Desenvolvimento

Use Docker Compose para compilar, executar e testar. Configure `.env` local a partir de `.env.example`, depois execute:

```bash
docker compose build
docker compose run --rm api dotnet restore GarageWatch.slnx --locked-mode
docker compose --profile test run --rm test
docker compose up --build
```

O serviço fica somente em `127.0.0.1:${APP_PORT}`; liveness, readiness e perfil local estão em `/health/live`, `/health/ready` e `/api/v1/system-profile`. T01-O adiciona métricas e traces OpenTelemetry; envio ao coletor é opcional via `OTEL_EXPORTER_OTLP_ENDPOINT` em `.env`. Sem endpoint, não há backend, retenção ou dashboard. Os lockfiles `packages.lock.json` estão versionados; restore usa modo travado. Para atualizar dependências, altere a versão central e regenere/revise os lockfiles antes de commit. Para parar, `docker compose down` (não há volume de dados). As decisões, limites e conteúdo estão em `docs/architecture.md`, `docs/configuration.md`, `docs/dependencies.md` e `contracts/`. Nunca use este repositório para armazenar vídeo pessoal ou segredos.

## Licença

Este repositório é público para consulta, mas ainda não possui licença de reutilização definida. Não presuma permissão para reutilizar o código; as dependências de terceiros mantêm suas licenças próprias.
