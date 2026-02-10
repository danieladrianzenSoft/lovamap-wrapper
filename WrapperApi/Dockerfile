FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# RUN apt-get update && \
#     apt-get install -y --no-install-recommends docker.io curl && \
#     rm -rf /var/lib/apt/lists/*

# Install curl and a recent Docker CLI (no daemon, just client)
ARG DOCKER_CLI_VERSION=28.5.2

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates && \
    rm -rf /var/lib/apt/lists/* && \
    curl -fsSL https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKER_CLI_VERSION}.tgz \
    | tar xz --strip-components=1 -C /usr/local/bin docker/docker && \
    chmod +x /usr/local/bin/docker

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "WrapperApi.dll"]