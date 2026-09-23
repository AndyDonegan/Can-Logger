# Microchip LIN Analyzer setup package

The connected device identifies as **LIN Bus Analyzer**, USB **04D8:0A04**.
Windows already provides its USB HID driver (`HidUsb`). Do not replace it with a
serial-port driver or redistribute Microsoft's system driver files.

Microchip publishes its application/support libraries as **LIN Serial Analyzer
v3.0.0**. The official ZIP contains `linanalyzer-1.1.0-windows-installer.exe`, whose
own product version is 1.1.0. This filename is from the vendor package, not a
substituted download. The original installer is not Authenticode-signed.

The downloaded original ZIP and installer are cached within the project at
`.vendor/microchip-lin/3.0.0/`. That cache is ignored by Git. `package.json`
records the official source and SHA-256 hashes so this exact package can be
obtained again. Hashes identify the inspected download; they are not vendor signatures.

From Windows PowerShell, in the project directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-microchip-lin.ps1
```

This verifies/downloads the pinned package, stages the installer locally, and
requests Windows administrator elevation. Approve UAC and complete the official
setup wizard. Use `-DownloadOnly` to populate/verify the cache without installation.
The script does not open the analyzer, change firmware, or send LIN commands.

## Release packaging

Include this manifest, documentation and installer script in the project release.
They retrieve the official package directly from Microchip. Permission to rebundle
the vendor installer/libraries has not yet been established; the locally cached
binaries are not automatically copied to application build/publish output. Preserve
all vendor and dependency notices if redistribution is subsequently approved.

## Official sources

- [LIN Analyzer v3.0.0 downloads](https://www.microchip.com/en-us/software-library/lin-analyzer)
- [APGDT001 product page](https://www.microchip.com/en-us/development-tool/apgdt001)
- [Release notes](https://ww1.microchip.com/downloads/aemDocuments/documents/OTH/SoftwareLibrary/lin_analyzer/Release%2BNotes%2Bfor%2Bthe%2BLIN%2BAnalyzer%2BTool%2BUtility%2Bv3.0.0.pdf)

## Testing without administrator access

The normal-user HID access check succeeded on this Windows computer: no elevation,
read access granted, and 65-byte USB input/output report buffers. These are USB
report sizes, not LIN frame lengths. No LIN frames were read and no device commands
were sent during this check.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-microchip-lin-access.ps1
```

This checks enumeration, opens a read-only handle and queries HID metadata, then
closes it. It does not use `RunAs`, bypass UAC, install drivers, or change system
settings. The Microchip GUI installer is optional for this direct-access test.
Actual reception still needs verified vendor protocol/library initialization and
LIN frame parsing; successful USB access alone does not prove LIN capture.

## In-app receiver (no administrator access)

The optional portable receiver is now implemented. Use
`python3 scripts/setup-lin-receiver.py` from WSL, then open the app’s
**LIN data** tab. This route does not run the GUI installer above. See
[setup, test results and limitations](../../docs/LIN-RECEIVE-TEST.md).
