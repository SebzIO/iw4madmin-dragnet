#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
PACKAGE_VERSION="${PACKAGE_VERSION:-}"

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

echo "Created ${ZIP_PATH}"
