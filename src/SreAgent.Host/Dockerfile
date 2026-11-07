# Build context is src/ (the Host references the other SreAgent.* projects):
#   docker build -f src/SreAgent.Host/Dockerfile src
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SreAgent.Core/*.csproj SreAgent.Core/
COPY SreAgent.Tools/*.csproj SreAgent.Tools/
COPY SreAgent.LlmClients/*.csproj SreAgent.LlmClients/
COPY SreAgent.Host/*.csproj SreAgent.Host/
RUN dotnet restore SreAgent.Host/SreAgent.Host.csproj
COPY SreAgent.Core/ SreAgent.Core/
COPY SreAgent.Tools/ SreAgent.Tools/
COPY SreAgent.LlmClients/ SreAgent.LlmClients/
COPY SreAgent.Host/ SreAgent.Host/
RUN dotnet publish SreAgent.Host/SreAgent.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SreAgent.Host.dll"]
