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
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | Execução de testes |
| xunit | 2.9.3 | Apache-2.0 | Testes unitários/integração |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | Descoberta/executor de testes |
| Microsoft.Extensions.Logging JSON console | runtime .NET 10 | Microsoft | Logging estruturado em stdout; sem sink de arquivo |

Os lockfiles `packages.lock.json` fixam dependências transitivas com hash. OpenTelemetry está ativo somente como SDK/instrumentação de desenvolvimento; não há collector, Prometheus exporter, dashboard ou store de telemetria. A versão de Prometheus ASP.NET Core disponível na consulta (28/10/2026) estava em beta e não foi adotada. Não há ORM, banco, Redis, Kafka, MongoDB, runtime de IA ou frontend nesta fundação. Banco durável e política de idempotência precisam ser decididos antes de implementar estado mutável; nenhum fake de memória será apresentado como durabilidade.
