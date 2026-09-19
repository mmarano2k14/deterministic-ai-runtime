FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY . .
RUN dotnet publish implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Multiplexed.AI.McpServer.Host.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/runtime/
RUN dotnet publish implementations/dotnet/workers/Multiplexed.AI.HostedInvocation.DotNetWorker/Multiplexed.AI.HostedInvocation.DotNetWorker.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/workers/dotnet/
RUN dotnet publish implementations/sdk/samples/mcp-effect-server/Multiplexed.AI.Samples.McpEffectServer/Multiplexed.AI.Samples.McpEffectServer.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/mcp-effect-server/

FROM node:22-bookworm-slim AS node-runtime
FROM python:3.12-slim-bookworm AS python-runtime

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=dotnet-build /out/runtime /app/runtime
COPY --from=dotnet-build /out/workers/dotnet /app/workers/dotnet
COPY --from=dotnet-build /out/mcp-effect-server /app/mcp-effect-server
COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
COPY --from=python-runtime /usr/local /usr/local
COPY implementations/node/workers/hosted_invocation /app/workers/typescript
COPY implementations/python/workers/hosted_invocation /app/workers/python
COPY implementations/matrix/runtime/docker/runtime-entrypoint.sh /app/runtime-entrypoint.sh
RUN chmod 0555 /app/runtime-entrypoint.sh \
 && chmod -R a-w /app/runtime /app/workers /app/mcp-effect-server \
 && mkdir -p /matrix/state
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080 8090
ENTRYPOINT ["/app/runtime-entrypoint.sh"]
