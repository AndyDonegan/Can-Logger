#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_dir=$(cd -- "$script_dir/.." && pwd)
vendor_dir="$project_dir/.vendor/waveshare"
download_dir=$(mktemp -d)
trap 'rm -rf -- "$download_dir"' EXIT

archive_url='https://files.waveshare.com/wiki/USB-CAN-FD/Demo/USB-CAN-FD_%20Library.zip'
archive_path="$download_dir/usb-can-fd-library.zip"

mkdir -p -- "$vendor_dir"
curl -fL "$archive_url" -o "$archive_path"
unzip -p "$archive_path" x64/ControlCANFD.dll > "$vendor_dir/ControlCANFD.dll"

echo "Installed Waveshare x64 API library at:"
echo "$vendor_dir/ControlCANFD.dll"
echo "Run 'dotnet build' to copy it into the application output."
