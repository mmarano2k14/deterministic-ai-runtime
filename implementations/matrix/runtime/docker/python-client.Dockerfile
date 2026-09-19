FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sample
WORKDIR /src
COPY . .
RUN dotnet publish implementations/sdk/samples/published-functions/dotnet/Multiplexed.AI.Samples.PublishedFunctions/Multiplexed.AI.Samples.PublishedFunctions.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/sample/
RUN dotnet publish implementations/sdk/samples/published-functions/dotnet/Multiplexed.AI.Samples.PublishedPackagedFunctions/Multiplexed.AI.Samples.PublishedPackagedFunctions.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/packaged/

FROM python:3.12-slim-bookworm AS final
WORKDIR /app
COPY implementations/python/sdk /app/implementations/python/sdk
RUN python -m pip install --no-cache-dir /app/implementations/python/sdk
COPY implementations/matrix/clients/python /app/implementations/matrix/clients/python
COPY implementations/sdk/samples/published-functions/typescript/functions.ts /matrix/samples/typescript/functions.ts
COPY implementations/sdk/samples/published-functions/python/functions.py /matrix/samples/python/functions.py
COPY implementations/matrix/runtime/docker/run-client.sh /app/run-client.sh
COPY --from=sample /out/sample/Multiplexed.AI.Samples.PublishedFunctions.dll /matrix/samples/dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll
COPY --from=sample /out/packaged/Multiplexed.AI.Samples.PublishedPackagedFunctions.dll /matrix/samples/dotnet/Multiplexed.AI.Samples.PublishedPackagedFunctions.dll
COPY --from=sample /out/packaged/Multiplexed.AI.Samples.PublishedDependency.dll /matrix/samples/dotnet/Multiplexed.AI.Samples.PublishedDependency.dll
RUN chmod 0555 /app/run-client.sh
ENV MATRIX_SAMPLE_ROOT=/matrix/samples
ENTRYPOINT ["/app/run-client.sh", "python"]
