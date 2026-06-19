#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
PACKAGE_VERSION="${PACKAGE_VERSION:-}"
VERSION_READER_DIR=""

cleanup() {
  if [[ -n "${VERSION_READER_DIR}" ]]; then
    rm -rf "${VERSION_READER_DIR}"
  fi
}
trap cleanup EXIT

if [[ -z "${PACKAGE_VERSION}" ]]; then
  PACKAGE_VERSION="$(
    dotnet msbuild "${ROOT_DIR}/src/Dragnet/Dragnet.csproj" \
      -nologo \
      -getProperty:Version
  )"
fi
PACKAGE_VERSION="${PACKAGE_VERSION#v}"

PACKAGE_NAME="Dragnet.IW4MAdmin.Plugin-${PACKAGE_VERSION}"
ARTIFACT_DIR="${ROOT_DIR}/artifacts"
PACKAGE_DIR="${ARTIFACT_DIR}/${PACKAGE_NAME}"
ZIP_PATH="${ARTIFACT_DIR}/${PACKAGE_NAME}.zip"
PLUGIN_DLL="${ROOT_DIR}/src/Dragnet/bin/${CONFIGURATION}/net10.0/Dragnet.dll"

if [[ "${SKIP_RESTORE:-0}" != "1" ]]; then
  dotnet restore "${ROOT_DIR}/src/Dragnet/Dragnet.csproj"
fi

dotnet build "${ROOT_DIR}/src/Dragnet/Dragnet.csproj" \
  -c "${CONFIGURATION}" \
  --no-restore \
  -p:Version="${PACKAGE_VERSION}" \
  -p:InformationalVersion="${PACKAGE_VERSION}"

rm -rf "${PACKAGE_DIR}"
mkdir -p "${PACKAGE_DIR}/Plugins" "${PACKAGE_DIR}/Configuration"

cp "${PLUGIN_DLL}" "${PACKAGE_DIR}/Plugins/Dragnet.dll"
cp "${ROOT_DIR}/README.md" "${PACKAGE_DIR}/README.md"
cp "${ROOT_DIR}/Configuration/DragnetSettings.example.json" "${PACKAGE_DIR}/Configuration/DragnetSettings.example.json"

cat > "${PACKAGE_DIR}/INSTALL.txt" <<'INSTALL'
Dragnet for IW4MAdmin

1. Copy Plugins/Dragnet.dll into your IW4MAdmin Plugins directory.
2. Start IW4MAdmin once so DragnetSettings is created, or copy Configuration/DragnetSettings.example.json to your IW4MAdmin Configuration directory and rename it to DragnetSettings.json.
3. Configure OriginName, PublicEndpoint, optional directory listing metadata, BootstrapPeers, and permission settings.
4. Restart IW4MAdmin.
5. If IW4MAdmin is behind a reverse proxy, enable WebSocket support for the IW4MAdmin webfront host.

See README.md for full setup and operational notes.
INSTALL

rm -f "${ZIP_PATH}"
(cd "${ARTIFACT_DIR}" && zip -qr "${ZIP_PATH}" "${PACKAGE_NAME}")

mapfile -t zip_entries < <(unzip -Z1 "${ZIP_PATH}")
expected_dll_entry="${PACKAGE_NAME}/Plugins/Dragnet.dll"
dll_entry_count=0
for entry in "${zip_entries[@]}"; do
  if [[ "${entry}" != "${PACKAGE_NAME}"/* ]]; then
    echo "ERROR: package contains unexpected root entry: ${entry}" >&2
    exit 1
  fi

  if [[ "${entry}" == "${expected_dll_entry}" ]]; then
    dll_entry_count=$((dll_entry_count + 1))
  fi
done

if [[ "${dll_entry_count}" -ne 1 ]]; then
  echo "ERROR: package must contain exactly one ${expected_dll_entry}; found ${dll_entry_count}" >&2
  exit 1
fi

VERSION_READER_DIR="$(mktemp -d)"
cat > "${VERSION_READER_DIR}/VersionReader.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
CSPROJ
cat > "${VERSION_READER_DIR}/Program.cs" <<'CS'
using System.Diagnostics;

if (args.Length != 1)
{
    return 2;
}

Console.Write(FileVersionInfo.GetVersionInfo(args[0]).ProductVersion ?? "");
return 0;
CS

packaged_version="$(
  dotnet run --project "${VERSION_READER_DIR}/VersionReader.csproj" -- "${PLUGIN_DLL}" \
    | tr -d '\r' \
    | cut -d '+' -f 1
)"
if [[ "${packaged_version}" != "${PACKAGE_VERSION}" ]]; then
  echo "ERROR: packaged DLL ProductVersion ${packaged_version} does not match package version ${PACKAGE_VERSION}" >&2
  exit 1
fi

echo "Validated ${ZIP_PATH}"
echo "Created ${ZIP_PATH}"
