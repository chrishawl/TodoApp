ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:9.0.305-noble

FROM ${DOTNET_SDK_IMAGE} AS sdk-base
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --uid 10001 --shell /bin/sh eval \
    && mkdir -p /codex-home /nuget /workspace \
    && chown -R eval:eval /codex-home /nuget /workspace
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    HOME=/tmp/home \
    DOTNET_CLI_HOME=/tmp/dotnet \
    NUGET_PACKAGES=/nuget/packages \
    XDG_DATA_HOME=/tmp/home/.local/share \
    XDG_CONFIG_HOME=/tmp/home/.config
COPY --chmod=755 docker/container-entrypoint /usr/local/bin/

FROM sdk-base AS agent
ARG CODEX_VERSION=0.147.0
RUN curl -fsSL https://chatgpt.com/codex/install.sh -o /tmp/install-codex.sh \
    && HOME=/root XDG_DATA_HOME=/root/.local/share XDG_CONFIG_HOME=/root/.config sh /tmp/install-codex.sh --release "${CODEX_VERSION}" \
    && install -m 755 /root/.codex/packages/standalone/releases/*/bin/codex /usr/local/bin/codex \
    && install -m 755 /root/.codex/packages/standalone/releases/*/bin/codex-code-mode-host /usr/local/bin/codex-code-mode-host \
    && install -m 755 /root/.codex/packages/standalone/releases/*/codex-path/rg /usr/local/bin/rg \
    && rm -rf /root/.local /root/.codex /tmp/install-codex.sh
COPY --chmod=755 docker/codex-isolated docker/seed-codex-auth /usr/local/bin/
USER eval
WORKDIR /workspace
ENTRYPOINT ["container-entrypoint"]

FROM sdk-base AS publish
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props TodoApp.Eval.sln ./
COPY src/TodoApp.Eval/ src/TodoApp.Eval/
COPY private-tests/ private-tests/
COPY fixtures/ fixtures/
COPY review-profiles/ review-profiles/
COPY calibration/ calibration/
RUN dotnet restore src/TodoApp.Eval/TodoApp.Eval.csproj --nologo \
    && dotnet publish src/TodoApp.Eval/TodoApp.Eval.csproj --no-restore --nologo -c Release -o /out

FROM sdk-base AS evaluator
COPY --from=publish /out/ /opt/todoapp-eval/
USER eval
WORKDIR /workspace
ENTRYPOINT ["container-entrypoint", "dotnet", "/opt/todoapp-eval/TodoApp.Eval.dll", "worker"]
