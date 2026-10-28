# Instruções do projeto garage-watch-platform

## Autoridade e escopo
- Repositório independente para a fundação do produto T01 e instrumentação observável T01-O; o protótipo `garage-watch` continua experimento T00a isolado.
- A autoridade de produto/arquitetura é `../../docs/` no repositório de contexto. Não contrariar ADRs sem registrar nova ADR antes ou com a implementação.
- T00-S está concluída; T00-H segue pendente e bloqueia somente o piloto real. Não alegar compatibilidade de câmera/NAS, desempenho, precisão ou validação 24/7.
- Implementar a base T01 e incrementos de fundação autorizados no backlog (atualmente T01-O, instrumentação dev-only). Não implementar RTSP, IA, reconhecimento, eventos completos, NAS, UI Android ou automação física sem uma tarefa desbloqueada e autorização de escopo.

## Engenharia
- .NET 10/C# e Docker Compose são a base inicial conforme contexto; imagem, pacotes e ferramentas devem ser fixados e documentados.
- Manter Domain/Application independentes de HTTP, persistência e SDKs de fornecedor. Introduzir portas somente em fronteiras concretas.
- Configuração por variáveis de ambiente; exemplos sem segredos. Nunca versionar `.env`, mídia, embeddings, logs sensíveis ou dados locais.
- Testes devem exercitar contratos e fluxo real interno; adaptadores fake somente nas fronteiras externas.
- Observabilidade deve usar dimensões limitadas, não registrar segredos/payload e nunca bloquear o fluxo do caso de uso; exportador/backend não é dependência de readiness.
- Atualizar documentação local e compartilhada, ADR, contratos e Mermaid canônico quando afetados.

## Verificações
- Executar formatação, build, testes, validação Compose e análise estática disponíveis em container. Não exigir instalação local de SDK para uso do projeto.
