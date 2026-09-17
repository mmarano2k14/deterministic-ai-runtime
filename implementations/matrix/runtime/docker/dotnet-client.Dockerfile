FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish implementations/matrix/fixtures/dotnet-worker/Multiplexed.AI.Matrix.Worker/Multiplexed.AI.Matrix.Worker.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/fixture/
RUN dotnet publish implementations/matrix/clients/dotnet/Multiplexed.AI.Matrix.DotNetClient/Multiplexed.AI.Matrix.DotNetClient.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/client/

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /out/client /app/client
COPY --from=build /out/fixture/Multiplexed.AI.Matrix.Worker.dll /matrix/fixtures/dotnet-worker/Multiplexed.AI.Matrix.Worker.dll
COPY implementations/matrix/fixtures/typescript-worker/main.ts /matrix/fixtures/typescript-worker/main.ts
COPY implementations/matrix/fixtures/python-worker/main.py /matrix/fixtures/python-worker/main.py
COPY implementations/matrix/runtime/docker/run-client.sh /app/run-client.sh
RUN chmod 0555 /app/run-client.sh
ENV MATRIX_FIXTURE_ROOT=/matrix/fixtures
ENTRYPOINT ["/app/run-client.sh", "dotnet"]
