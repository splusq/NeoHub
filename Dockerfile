# Build stage: runs on the build machine's own architecture and cross-compiles
# for the target, so multi-arch builds don't need QEMU emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Copy project files
COPY ["NeoHub/NeoHub/NeoHub.csproj", "NeoHub/NeoHub/"]
COPY ["NeoHub/TLink/DSC.TLink.csproj", "NeoHub/TLink/"]

# Restore dependencies
RUN dotnet restore "NeoHub/NeoHub/NeoHub.csproj" -a $TARGETARCH

# Copy all source code
COPY . .

# GitHub Actions metadata passed in at docker build time
ARG GITHUB_SHA
ARG GITHUB_RUN_NUMBER

# Build and publish. Restores again now that the .razor sources are present:
# the SDK only adds the package that ships _framework/blazor.web.js when it sees them.
WORKDIR "/src/NeoHub/NeoHub"
RUN dotnet publish "NeoHub.csproj" -c Release -a $TARGETARCH -o /app/publish \
    /p:UseAppHost=false /p:GITHUB_SHA=$GITHUB_SHA /p:GITHUB_RUN_NUMBER=$GITHUB_RUN_NUMBER \
 && mkdir -p /app/persist

# Runtime stage: Ubuntu chiseled image. No shell or package manager, runs as
# the non-root "app" user (UID 1654), invariant globalization, no tzdata.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

# Expose ports
EXPOSE 8080
EXPOSE 8443
EXPOSE 3072

# Copy published app
COPY --from=build /app/publish .

# Persist directory, writable by the app user, declared as a volume.
# A bind-mounted host folder must be writable by UID 1654.
COPY --from=build --chown=$APP_UID:$APP_UID /app/persist /app/persist
VOLUME /app/persist

ENTRYPOINT ["dotnet", "NeoHub.dll"]
