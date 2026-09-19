FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish implementations/sdk/samples/published-functions/dotnet/Multiplexed.AI.Samples.PublishedFunctions/Multiplexed.AI.Samples.PublishedFunctions.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/sample/
RUN dotnet publish implementations/matrix/clients/dotnet/Multiplexed.AI.Matrix.DotNetClient/Multiplexed.AI.Matrix.DotNetClient.csproj -c Release --nologo --verbosity quiet -p:PublishDir=/out/client/

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /out/client /app/client
COPY --from=build /out/sample/Multiplexed.AI.Samples.PublishedFunctions.dll /matrix/samples/dotnet/Multiplexed.AI.Samples.PublishedFunctions.dll
COPY implementations/sdk/samples/published-functions/typescript/functions.ts /matrix/samples/typescript/functions.ts
COPY implementations/sdk/samples/published-functions/python/functions.py /matrix/samples/python/functions.py
COPY implementations/matrix/runtime/docker/run-client.sh /app/run-client.sh
RUN chmod 0555 /app/run-client.sh
ENV MATRIX_SAMPLE_ROOT=/matrix/samples
ENTRYPOINT ["/app/run-client.sh", "dotnet"]
