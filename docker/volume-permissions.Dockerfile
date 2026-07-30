FROM mcr.microsoft.com/dotnet/aspnet:10.0

USER root

ENTRYPOINT ["/bin/sh", "-c", "chown -R app:app /data/uploads /keys && chown -R 10001:10001 /data/archive"]
