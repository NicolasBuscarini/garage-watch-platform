# Dependências fixadas — T01

| Componente | Versão | Licença/fonte | Uso e limite |
|---|---|---|---|
| .NET SDK container | 10.0.403, `mcr.microsoft.com/dotnet/sdk` por digest | Microsoft; imagem fixada em `Dockerfile` | Build, teste e execução dev local; patch LTS precisa atualização explícita |
| .NET runtime | 10.0.14 no SDK release de 25/10/2026 | Microsoft; imagem SDK acima | Runtime do host de desenvolvimento. Suporte oficial .NET 10 LTS até 14/11/2028 |
| MediatR | 12.4.1 | Apache-2.0 no nuspec; versão fixa em `Directory.Packages.props` | Despacho de use cases. Revisar licença/versão antes de atualização |
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
| Microsoft.Extensions.Logging JSON console | runtime .NET 10 | Microsoft | Logging estruturado em stdout; sem sink de arquivo |
| FFmpeg | Ubuntu `7:6.1.1-3ubuntu5` | `ffmpeg -L` confirmou build GPL-2-or-later (`--enable-gpl`, `--enable-libx264`); consultar notices da imagem e dependências transitivas | Segmentação RTSP/MPEG-TS por cópia e publisher sintético H.264 no T02. Publisher do teste gera Constrained Baseline/yuv420p para decodificação MSE ampla. Revisar obrigações de licença antes de distribuir a imagem. |
| MediaMTX | `bluenviron/mediamtx:1.12.3`, digest `sha256:3634a1eed1288b93e8d22ed74694eae96d483fcf676cac5d0c91830ad1b86b3d` | MIT, licença upstream v1.12.3 | Servidor RTSP somente no perfil `rtsp-contract` do Compose; não é serviço ativo de produto nem endpoint/câmera real. |
| HLS.js | 1.7.3, `wwwroot/vendor/hls.min.js`, SHA-256 `de49f5a3084f73c9e3e4e2999d6e789f8ea5aff0120d295631ecb9bf1480b3ea` | Apache-2.0; fonte npm `hls.js@1.7.3`, SRI tarball SHA-512 `MsPlx6yVW4Qv4C7mEVou4/gk/5cN2dVxOTnrNjPSmyH2f0Ln+/UkG10Ax1PE6OvrmTVPKgQYozTtBaPOuCzq7A==`; notice incluído em `wwwroot/vendor/HLSJS-LICENSE.txt` | Bundle servido same-origin para HLS via MSE; foi removida somente a referência source-map não distribuída. Sem CDN/runtime externo. Buffers limitados; desempenho/compatibilidade ainda carecem de CT02. |
| Bootstrap | 5.3.8, `wwwroot/vendor/bootstrap.min.css`, SHA-256 `a7695108838ea4708a547d2d07fb37c4889eb663fb18c3761677ed6a50c4fe41` | MIT; distribuição oficial `getbootstrap.com`, notice incluído em `wwwroot/vendor/BOOTSTRAP-LICENSE.txt` | Somente CSS minificado local para layout/utilitários/formulários; sem CDN, runtime JS ou build Node. Customizações do tema/player em `wwwroot/app.css`. |

Os lockfiles `packages.lock.json` fixam dependências transitivas com hash. OpenTelemetry está ativo somente como SDK/instrumentação de desenvolvimento; não há collector, Prometheus exporter, dashboard ou store de telemetria. A versão de Prometheus ASP.NET Core disponível na consulta (28/10/2026) estava em beta e não foi adotada. T03-IAM adiciona EF Core somente para a fronteira relacional SQLite de identidade, grants e auditoria local. SQLite é single-host/single-process nesta etapa; SQL Server permanece candidato de migração se operação multi-instância/serviço independente exigir. Nenhum Redis, Kafka, MongoDB, runtime de IA ou frontend framework foi introduzido; cadastro e UI administrativa seguem posteriores.
