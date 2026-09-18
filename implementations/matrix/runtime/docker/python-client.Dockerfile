FROM mcr.microsoft.com/dotnet/sdk:10.0 AS fixture
WORKDIR /src
COPY . .
RUN dotnet publish implementations/matrix/fixtures/dotnet-worker/Multiplexed.AI.Matrix.Worker/Multiplexed.AI.Matrix.Worker.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/fixture/
RUN dotnet publish implementations/matrix/fixtures/dotnet-packaged-worker/Multiplexed.AI.Matrix.PackagedWorker/Multiplexed.AI.Matrix.PackagedWorker.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/packaged/

FROM python:3.12-slim-bookworm AS final
WORKDIR /app
COPY implementations/python/sdk /app/implementations/python/sdk
RUN python -m pip install --no-cache-dir /app/implementations/python/sdk
COPY implementations/matrix/clients/python /app/implementations/matrix/clients/python
COPY implementations/matrix/fixtures/typescript-worker/main.ts /matrix/fixtures/typescript-worker/main.ts
COPY implementations/matrix/fixtures/python-worker/main.py /matrix/fixtures/python-worker/main.py
COPY implementations/matrix/runtime/docker/run-client.sh /app/run-client.sh
COPY --from=fixture /out/fixture/Multiplexed.AI.Matrix.Worker.dll /matrix/fixtures/dotnet-worker/Multiplexed.AI.Matrix.Worker.dll
COPY --from=fixture /out/packaged/Multiplexed.AI.Matrix.PackagedWorker.dll /matrix/fixtures/dotnet-packaged-worker/Multiplexed.AI.Matrix.PackagedWorker.dll
COPY --from=fixture /out/packaged/Multiplexed.AI.Matrix.Dependency.dll /matrix/fixtures/dotnet-packaged-worker/Multiplexed.AI.Matrix.Dependency.dll
RUN chmod 0555 /app/run-client.sh
ENV MATRIX_FIXTURE_ROOT=/matrix/fixtures
ENTRYPOINT ["/app/run-client.sh", "python"]
