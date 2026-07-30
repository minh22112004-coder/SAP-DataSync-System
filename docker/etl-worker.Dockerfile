FROM python:3.13-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends \
        ca-certificates curl tzdata \
    && curl --fail --silent --show-error --location \
        --output /tmp/packages-microsoft-prod.deb \
        https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install --yes --no-install-recommends msodbcsql18 \
    && rm -rf /var/lib/apt/lists/* /tmp/packages-microsoft-prod.deb

RUN useradd --create-home --uid 10001 app

WORKDIR /app
COPY src/EtlWorker/requirements.txt ./requirements.txt
RUN pip install --no-cache-dir --requirement requirements.txt

COPY src/EtlWorker/ ./

USER app
EXPOSE 8090
ENTRYPOINT ["python", "-m", "etl_worker.main"]
