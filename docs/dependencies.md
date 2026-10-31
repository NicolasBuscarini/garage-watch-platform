# Dependências fixadas — T01

| Componente | Versão | Licença/fonte | Uso e limite |
|---|---|---|---|
| .NET SDK container | 10.0.403, `mcr.microsoft.com/dotnet/sdk` por digest | Microsoft; imagem fixada em `Dockerfile` | Build, teste e execução dev local; patch LTS precisa atualização explícita |
| .NET runtime | 10.0.14 no SDK release de 25/10/2026 | Microsoft; imagem SDK acima | Runtime do host de desenvolvimento. Suporte oficial .NET 10 LTS até 14/11/2028 |
| MediatR | 12.4.1 | Apache-2.0 no nuspec; versão fixa em `Directory.Packages.props` | Despacho de use cases. Revisar licença/versão antes de atualização |
| Microsoft.ML.OnnxRuntime | 1.31.1 | MIT, NuGet oficial | Runtime ONNX CPU dentro do adaptador de inferência; não há modelo/pesos incluídos ou selecionados |
| OpenTelemetry.Extensions.Hosting | 1.20.1 | Apache-2.0 no nuspec | Registro do SDK com ciclo de vida do host |
| OpenTelemetry.Instrumentation.AspNetCore | 1.20.1 | Apache-2.0 no nuspec | Spans de entrada HTTP; sem captura deliberada de headers/body |
| OpenTelemetry.Instrumentation.Runtime | 1.20.1 | Apache-2.0 no nuspec | Métricas de runtime .NET/GC/processo |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.20.1 | Apache-2.0 no nuspec | Exportador opcional OTLP HTTP-Protobuf de spans e métricas; desligado sem endpoint |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | MIT | Host de teste de integração |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.14 | Apache-2.0 (NuGet) | Persistência de usuários/roles ASP.NET Core Identity; senha hash via PasswordHasher framework |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.14 | MIT (NuGet) | Provider relacional SQLite para store IAM local, sem serviço DB adicional |
| Microsoft.EntityFrameworkCore.Design | 10.0.14 | MIT (NuGet) | Design-time para migrations versionadas; PrivateAssets=all |
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | Execução de testes |
| xunit | 2.9.3 | Apache-2.0 | Testes unitários/integração |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | Descoberta/executor de testes |
| NSwag.AspNetCore | 14.7.1 | MIT, licença upstream | Geração OpenAPI/Swagger UI para Minimal APIs; middleware e assets same-origin, Development/Testing e Administrator-only; sem serviço Node/externo |
| Microsoft.Extensions.Logging JSON console | runtime .NET 10 | Microsoft | Logging estruturado em stdout; sem sink de arquivo |
| FFmpeg | Ubuntu `7:6.1.1-3ubuntu5` | `ffmpeg -L` confirmou build GPL-2-or-later (`--enable-gpl`, `--enable-libx264`); consultar notices da imagem e dependências transitivas | Segmentação RTSP/MPEG-TS por cópia e publisher sintético H.264 no T02. Publisher do teste gera Constrained Baseline/yuv420p para decodificação MSE ampla. Revisar obrigações de licença antes de distribuir a imagem. |
| MediaMTX | `bluenviron/mediamtx:1.12.3`, digest `sha256:3634a1eed1288b93e8d22ed74694eae96d483fcf676cac5d0c91830ad1b86b3d` | MIT, licença upstream v1.12.3 | Servidor RTSP somente no perfil `rtsp-contract` do Compose; não é serviço ativo de produto nem endpoint/câmera real. |
| Frontend `garage-watch-web` | React 19.3.1, Vite 8.3.5, TypeScript 7.0.2, submodule `web/` no commit gitlink | Repositório público independente; sem licença de reutilização. Detalhes/lockfile em `web/docs/dependencies.md` | Node não roda em produção; imagem compila e API serve o bundle estático same-origin em `/app/web`. |
| Node build stage | 24.22.2-bookworm-slim, imagem fixada por digest em `Dockerfile` e `compose.yaml` | Docker Official Image / Debian; `web/docs/dependencies.md` | `npm ci` e Vite build somente durante build da imagem. O profile `web-dev` é o único runtime Node, opt-in e loopback. |
| Bootstrap CSS | 5.3.8, pacote `bootstrap` local no submodule `web/` | MIT; notice e lockfile em `web/docs/dependencies.md` | CSS incluído no bundle frontend; sem CDN ou JavaScript de Bootstrap. |
| HLS.js | 1.7.3, pacote `hls.js` local no submodule `web/` | Apache-2.0; notice e lockfile em `web/docs/dependencies.md` | Chunk HLS carregado sob demanda e servido same-origin; XHR inclui cookies same-origin. HLS nativo segue fallback Safari. |

Os lockfiles `packages.lock.json` fixam dependências transitivas com hash. OpenTelemetry está ativo somente como SDK/instrumentação de desenvolvimento; não há collector, Prometheus exporter, dashboard ou store de telemetria. A versão de Prometheus ASP.NET Core disponível na consulta (28/10/2026) estava em beta e não foi adotada. T03-IAM adiciona EF Core somente para a fronteira relacional SQLite de identidade, grants e auditoria local. SQLite é single-host/single-process nesta etapa; SQL Server permanece candidato de migração se operação multi-instância/serviço independente exigir. ONNX Runtime CPU está presente como adaptador opt-in, mas nenhum modelo/peso foi selecionado ou incluído. Nenhum Redis, Kafka, MongoDB, runtime de interpretação de clipe ou frontend framework foi introduzido; cadastro e UI administrativa seguem posteriores.
