FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /source

# 프로젝트 참조를 먼저 복원하고 빌드 산출물/비밀 파일은 context에서 제외한다.
COPY TranslaCat.Chat.Domain/TranslaCat.Chat.Domain.csproj TranslaCat.Chat.Domain/
COPY TranslaCat.Chat.Application/TranslaCat.Chat.Application.csproj TranslaCat.Chat.Application/
COPY TranslaCat.Chat.Infrastructure/TranslaCat.Chat.Infrastructure.csproj TranslaCat.Chat.Infrastructure/
COPY TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj TranslaCat.Chat.Api/
RUN dotnet restore TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj

COPY TranslaCat.Chat.Domain/ TranslaCat.Chat.Domain/
COPY TranslaCat.Chat.Application/ TranslaCat.Chat.Application/
COPY TranslaCat.Chat.Infrastructure/ TranslaCat.Chat.Infrastructure/
COPY TranslaCat.Chat.Api/ TranslaCat.Chat.Api/
RUN dotnet publish TranslaCat.Chat.Api/TranslaCat.Chat.Api.csproj --no-restore -c Release -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS runtime
WORKDIR /app
COPY --from=build /publish/ ./
COPY --chmod=0555 deploy/chat/start-chat-api.sh /app/start-chat-api.sh
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/bin/sh", "/app/start-chat-api.sh"]
