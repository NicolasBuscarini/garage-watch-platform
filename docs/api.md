# API HTTP e OpenAPI

A API mantém Minimal APIs; não há MVC controllers. Cada operação tem uma classe/arquivo em `src/GarageWatch.Api/Endpoints/`, herda de `BaseEndpoint` e é registrada explicitamente em `ApiEndpointRegistration`. Essa lista é o índice explícito dos handlers. `BaseEndpoint` compartilha apenas tag/domínio, operationId, resumo/descrição e resposta de erro segura 500; request/response, status, autenticação e lógica continuam declarados em cada endpoint.

## Visualização local

Com a API Platform ativa pelo `./garage up`, autentique-se como Administrator e acesse:

- Swagger UI: `http://127.0.0.1:8081/docs` (ou a porta Platform configurada no `.env` raiz).
- Documento OpenAPI 3 JSON: `http://127.0.0.1:8081/openapi/v1.json`.

Ambos existem somente nos ambientes ASP.NET Core Development/Testing e exigem cookie de Administrator. A UI é same-origin; “Try it out” usa a sessão já autenticada. A UI/documento não ampliam autorização: cada rota conserva sua policy e verificações de same-origin. Em Production as rotas de documentação não são registradas. Não encaminhe essas URLs para LAN/Internet.

## Domínios expostos

| Tag | Escopo |
|---|---|
| Health | Liveness público mínimo e readiness de captura Admin-only |
| Authentication | Login/logout, registro local, estado de sessão e identidade atual |
| Clients | Heartbeat de presença efêmera |
| Cameras | Câmeras visíveis na sessão |
| Camera administration | CRUD lógico e paginação de contextos/câmeras |
| User administration | Consulta de contas, atribuições a perfis/escopos e desativação Admin-only |
| Access requests | Pedido autenticado User, consulta do próprio estado e listagem/revisão Admin-only |
| Access profiles | Catálogo delegável, criação de perfis nomeados e publicação de versões imutáveis Admin-only |
| Live video | Playlist HLS e segmentos com suporte Range, após autorização por câmera |
| Clip analysis | Política versionada por câmera e pedido idempotente de análise de clipe íntegro |
| Clips | Timeline paginada, filtro de período/estado/câmera, detalhe, thumbnail e mídia; autenticação + `clips.read` por par câmera/contexto; hash validado e Range na mídia |
| Clip trigger | `GET/PUT /api/v1/admin/cameras/{cameraId}/clip-trigger`; Administrator-only, PUT same-origin e versionado, configuração habilitada persistida por câmera; aplicada no próximo segmento sem reiniciar captura |
| System | Perfil sanitizado do serviço |

Cada operação publicada inclui operationId único, resumo/descrição, parâmetros/JSON body quando aplicável, schemas concretos e status de sucesso/erro. Respostas HLS/clipe declaram seus media types e Range declara `206`. Endpoints de clipes são dev-only e limitam listagem a 10.000 manifests por chamada, páginas a 100 itens e mídia a IDs locais; não aceitam caminho fornecido pelo cliente.

Gestão de acesso usa `GET /api/v1/admin/permission-catalog`, `GET/POST /api/v1/admin/access-rules`, `POST /api/v1/admin/access-rules/{profileId}/versions` e `PUT /api/v1/admin/users/{userId}/access-rules`. `GET /api/v1/admin/users` aceita `page`/`pageSize` (máximo 100) e filtro opcional `userId`; o resultado continua minimizado e paginado. Todos os IDs desses parâmetros/rotas são IDs públicos tipados, não PKs. A atribuição envia `profileVersionPublicId` e escopos `{contextId,cameraId}`; os IDs de contexto/câmera nesses escopos também são públicos. Request de usuário não aceita permission keys. Administrator é reservado e não pode receber perfil. A migração mantém os grants legados e cria atribuições ao perfil somente-vídeo com o mesmo escopo; depois da migração, autorização efetiva consulta somente perfis. `live.audio.listen` não é delegável até existir rendition isolada de áudio no servidor.

Pedidos locais de acesso usam `POST /api/v1/access-requests` com `{idempotencyKey,purpose}` (`live_monitoring` ou `camera_access`), `GET /api/v1/access-requests/mine`, `GET /api/v1/admin/access-requests` e `POST /api/v1/admin/access-requests/{requestId}/review`. O POST exige Origin same-origin, sessão de User e no máximo 3 tentativas/minuto por identidade; o SQLite guarda um pedido pendente por conta e a auditoria na mesma transação. Reenvio idempotente/de pedido pendente retorna o registro existente; payload divergente para a mesma chave retorna conflito. Marcar como revisado não concede acesso: o Admin deve atribuir perfil/escopo separadamente em Usuários. Sem provedor/endereço (P13/P21), nenhuma mensagem de e-mail é enviada; pedidos permanecem disponíveis na lista Admin.

T03-PUBLIC-IDS aplica IDs públicos imutáveis com prefixo por namespace (`USR`, `CTX`, `CAM`, `PRF`, `PVN`, `ASN`, `ARQ`, `CLP`, `JOB`) e 12 dígitos gerados via CSPRNG. As respostas expõem `publicId` (ou o campo específico terminado em `PublicId`); PKs GUID/legadas permanecem internas e são rejeitadas em URLs, filtros e payloads públicos. A migração/backfill do SQLite e manifests preserva as chaves internas, tem índices únicos no IAM e alocação de CLP com reserva/retry limitado no spool local. Esses IDs não são mecanismo de autorização.

T04B expõe `GET/PUT /api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy` e `POST /api/v1/clips/{clipId}/analysis-requests`. GET exige `clips.read` no par câmera/contexto e retorna a política efetiva (`manual_only`, versão 0, quando ainda não persistida) e flags `canRequest`/`canManage` calculadas no servidor. PUT aceita somente `always`, `large_motion`, `manual_only` ou `never`, exige `clips.analysis.policy.manage` no par câmera/contexto, versão esperada e Origin same-origin; alteração incrementa a versão e grava auditoria. O pedido exige `clips.analysis.request`, chave idempotente e opcional `analysisVersion`, valida manifesto `ready`/checksum no spool, resolve o escopo do catálogo e insere no máximo um job por clipe+versão; repetição devolve `existing=true`. `never` responde conflito sanitizado e é negado inclusive para Administrator. Backlog cheio responde `analysis_capacity_reached` sem criar job; escopo cruzado responde como clipe não encontrado. `large_motion` não cria job automático enquanto o manifesto não trouxer evidência técnica versionada; `always` e `manual_only` funcionam como descrito, mas nenhum interpretador/modelo é executado neste incremento.
