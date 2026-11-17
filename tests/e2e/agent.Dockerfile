# Linux agent for the end-to-end test (PLAN.md S14, task T508).
#
# Builds QoffeeAutomat.Agent from the repository (build context: the repository root) and runs
# it with the simulated platform. Scripts run through PowerShell 7, so `pwsh` is installed from
# the PowerShell .NET tool package (NuGet; no download from another host at run time).
# For tests only: this image is not a release artifact.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PWSH_VERSION=7.6.*
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src ./src
RUN dotnet publish src/QoffeeAutomat.Agent/QoffeeAutomat.Agent.csproj \
      --configuration Release --framework net10.0 --output /out/agent -p:UseAppHost=false
RUN dotnet tool install PowerShell --version "${PWSH_VERSION}" --tool-path /out/pwsh

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/agent /app
COPY --from=build /out/pwsh /opt/pwsh
RUN ln -s /opt/pwsh/pwsh /usr/local/bin/pwsh
COPY tests/e2e/agent/seed /seed
COPY --chmod=0755 tests/e2e/agent/entrypoint.sh /entrypoint.sh
RUN mkdir /data && chown app:app /data
USER app
ENV QOFFEEAUTOMAT_DATA_DIR=/data \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    POWERSHELL_TELEMETRY_OPTOUT=1 \
    POWERSHELL_UPDATECHECK=Off
VOLUME ["/data"]
EXPOSE 8481
ENTRYPOINT ["/entrypoint.sh"]
