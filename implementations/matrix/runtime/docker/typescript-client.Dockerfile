FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sample
WORKDIR /src
COPY . .
RUN dotnet publish implementations/sdk/samples/published-functions/dotnet/Multiplexed.AI.Samples.PublishedFunctions/Multiplexed.AI.Samples.PublishedFunctions.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/sample/

FROM node:22-bookworm-slim AS build
WORKDIR /app
COPY implementations/node/sdk /app/implementations/node/sdk
RUN cd /app/implementations/node/sdk && npm ci && npm run build
COPY implementations/matrix/clients/typescript /app/implementations/matrix/clients/typescript
COPY implementations/sdk/samples/published-functions/typescript/functions.ts /matrix/samples/typescript/functions.ts
COPY implementations/sdk/samples/published-functions/python/functions.py /matrix/samples/python/functions.py
COPY implementations/matrix/runtime/docker/run-client.sh /app/run-client.sh
COPY --from=sample /out/sample/Multiplexed.AI.Samples.PublishedFunctions.dll /matrix/samples/dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll
RUN chmod 0555 /app/run-client.sh
ENV MATRIX_SAMPLE_ROOT=/matrix/samples
ENTRYPOINT ["/app/run-client.sh", "typescript"]
