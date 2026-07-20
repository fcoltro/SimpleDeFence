# Containerized build environment for the current .NET Framework 4.8 app.
#
# Requires Docker Desktop set to WINDOWS containers (net48 does not run on Linux containers).
# Build:
#   docker build -t simpledefence-build .
# Extract build output to .\out on the host:
#   docker run --rm -v "${PWD}\out:C:\out" simpledefence-build
#
# This mirrors the steps in .github/workflows/build.yml.

# escape=`

FROM mcr.microsoft.com/dotnet/framework/sdk:4.8-windowsservercore-ltsc2022 AS build
SHELL ["powershell", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

# Chocolatey + WiX v3 Toolset (needed for the MsiSetup project)
RUN Set-ExecutionPolicy Bypass -Scope Process -Force; `
    iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1')); `
    choco install wixtoolset -y --no-progress

WORKDIR C:\src
COPY . .

RUN dotnet restore SimpleDeFence/SimpleDeFence.csproj

ARG CONFIGURATION=Release
RUN msbuild SimpleDeFence/SimpleDeFence.csproj /p:Configuration=$env:CONFIGURATION /p:RestorePackages=false

CMD Copy-Item -Recurse -Force C:\src\SimpleDeFence\bin\* C:\out\
