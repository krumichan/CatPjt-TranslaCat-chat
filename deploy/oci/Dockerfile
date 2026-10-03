# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS tested
WORKDIR /source
COPY TranslaCat.Chat.Domain/ TranslaCat.Chat.Domain/
COPY TranslaCat.Chat.Application/ TranslaCat.Chat.Application/
COPY TranslaCat.Chat.Infrastructure/ TranslaCat.Chat.Infrastructure/
COPY TranslaCat.Chat.Api/ TranslaCat.Chat.Api/
COPY TranslaCat.Chat.UnitTests/ TranslaCat.Chat.UnitTests/
COPY TranslaCat.Chat.ApiTests/ TranslaCat.Chat.ApiTests/
RUN dotnet test TranslaCat.Chat.UnitTests/TranslaCat.Chat.UnitTests.csproj -c Release \
 && dotnet test TranslaCat.Chat.ApiTests/TranslaCat.Chat.ApiTests.csproj -c Release \
 && dotnet publish TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj -c Release -o /publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=tested /publish/ ./
COPY --chmod=0555 deploy/oci/entrypoint.sh /app/entrypoint.sh
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_ENVIRONMENT=Production ASPNETCORE_ENVIRONMENT=Production
USER 10001:10001
EXPOSE 8080
ENTRYPOINT ["/app/entrypoint.sh"]
