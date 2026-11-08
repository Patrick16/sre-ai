# One Dockerfile for all sandbox services; the build context is src/Sandbox.Services (the services share Shared/):
#   docker build -f src/Sandbox.Services/Dockerfile --build-arg DIR=Frontend --build-arg PROJECT=Sandbox.Frontend src/Sandbox.Services
# Stage 1 has the SDK (~1 GB) and compiles; stage 2 has only the runtime, so the final image stays small.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
ARG DIR
WORKDIR /src
# The .csproj first: restore is cached as long as the dependencies don't change.
COPY $DIR/*.csproj $DIR/
RUN dotnet restore "$DIR/$PROJECT.csproj"
COPY Shared/ Shared/
COPY $DIR/ $DIR/
RUN dotnet publish "$DIR/$PROJECT.csproj" -c Release -o /app --no-restore

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
