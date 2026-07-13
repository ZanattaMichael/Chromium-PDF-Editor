# Base image ships the .NET 8 SDK plus common dev tooling (git, curl, non-root
# "vscode" user with passwordless sudo). Node.js, Python, and PowerShell are layered
# on via devcontainer Features in devcontainer.json rather than here, so their
# versions stay declarative and easy to bump independently of this Dockerfile.
FROM mcr.microsoft.com/devcontainers/dotnet:1-8.0-bookworm

# zip/unzip: used by scripts/package-extension.sh to build the Chrome Web Store package.
RUN apt-get update && apt-get install -y --no-install-recommends \
        zip \
        unzip \
    && rm -rf /var/lib/apt/lists/*
