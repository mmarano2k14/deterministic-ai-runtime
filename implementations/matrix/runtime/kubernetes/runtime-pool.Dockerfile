FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY . .
RUN dotnet publish implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Multiplexed.AI.McpServer.Host.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/runtime/
RUN dotnet publish implementations/dotnet/workers/Multiplexed.AI.HostedInvocation.DotNetWorker/Multiplexed.AI.HostedInvocation.DotNetWorker.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/workers/dotnet/

FROM node:22-bookworm-slim AS node-runtime
FROM python:3.12-slim-bookworm AS python-runtime

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=dotnet-build /out/runtime /app
COPY --from=dotnet-build /out/workers/dotnet /app/workers/dotnet
COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
COPY --from=python-runtime /usr/local /usr/local
COPY implementations/node/workers/hosted_invocation /app/workers/typescript
COPY implementations/python/workers/hosted_invocation /app/workers/python
RUN chmod -R a-w /app/workers
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "/app/Multiplexed.AI.McpServer.Host.dll"]
