FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY global.json ./
COPY src/Importer/SapDataSync.Importer.csproj src/Importer/
RUN dotnet restore src/Importer/SapDataSync.Importer.csproj

COPY src/Importer/ src/Importer/
RUN dotnet publish src/Importer/SapDataSync.Importer.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

USER app
ENTRYPOINT ["dotnet", "SapDataSync.Importer.dll"]
