# One Dockerfile for all sandbox services; the build context is the service's own folder:
#   docker build -f src/Sandbox.Services/Dockerfile --build-arg PROJECT=Sandbox.Frontend src/Sandbox.Services/Frontend
# Stage 1 has the SDK (~1 GB) and compiles; stage 2 has only the runtime, so the final image stays small.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish "$PROJECT.csproj" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT
WORKDIR /app
COPY --from=build /app ./
# Built-in non-root user of the .NET images; the pod spec additionally enforces runAsNonRoot.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    APP_DLL=$PROJECT.dll
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
