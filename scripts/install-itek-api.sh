#!/usr/bin/env bash
set -euo pipefail

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_dir=$(cd -- "$script_dir/.." && pwd)
vendor_dir="$project_dir/.vendor/itek"
download_dir=$(mktemp -d)
trap 'rm -rf -- "$download_dir"' EXIT

archive_url='https://itekon1.oss-cn-hangzhou.aliyuncs.com/%E4%BA%8C%E6%AC%A1%E5%BC%80%E5%8F%91%E4%BE%8B%E7%A8%8B/API_Samples%2020220222.rar'
archive_sha256='0293c03bf4c80b29f5900e086a156943b09cd63d5ca4aac1ba8f5adb815422f6'
archive_path="$download_dir/itek-api-samples.rar"
seven_zip_archive="$download_dir/7zip.tar.xz"
seven_zip_url='https://www.7-zip.org/a/7z2602-linux-x64.tar.xz'
extract_dir="$download_dir/extracted"

curl -fL "$archive_url" -o "$archive_path"
actual_sha256=$(sha256sum "$archive_path" | cut -d' ' -f1)
if [[ "$actual_sha256" != "$archive_sha256" ]]; then
    echo "iTEK API archive checksum mismatch." >&2
    echo "Expected: $archive_sha256" >&2
    echo "Actual:   $actual_sha256" >&2
    exit 1
fi

curl -fL "$seven_zip_url" -o "$seven_zip_archive"
tar -xJf "$seven_zip_archive" -C "$download_dir"
mkdir -p -- "$extract_dir" "$vendor_dir/kerneldlls"

"$download_dir/7zz" x -y "-o$extract_dir" "$archive_path" \
    'API_Samples 20220222/QTTest_VS2022_Qt6.8_X64/QTTest(VS)2022/Test/lib/kerneldlls/usbcan.dll' \
    >/dev/null

source_dir="$extract_dir/API_Samples 20220222/QTTest_VS2022_Qt6.8_X64/QTTest(VS)2022/Test/lib"
install -m 0644 "$source_dir/kerneldlls/usbcan.dll" "$vendor_dir/kerneldlls/usbcan.dll"

echo "Installed the official iTEK x64 API at:"
echo "$vendor_dir/kerneldlls/usbcan.dll"
echo "Run 'dotnet build' to copy it into the application output."
