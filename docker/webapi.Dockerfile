FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY global.json ./
COPY src/WebApi/SapDataSync.WebApi.csproj src/WebApi/
RUN dotnet restore src/WebApi/SapDataSync.WebApi.csproj

COPY src/WebApi/ src/WebApi/
RUN dotnet publish src/WebApi/SapDataSync.WebApi.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./
COPY database/scripts/ ./database/scripts/
RUN mkdir -p /keys /data/uploads \
    && chown -R app:app /keys /data/uploads

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "SapDataSync.WebApi.dll"]
