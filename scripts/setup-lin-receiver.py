#!/usr/bin/env python3
"""Fetch and compile the optional Windows/WSL LIN receiver without an installer."""
import hashlib
import os
from pathlib import Path
import subprocess
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / '.vendor/microchip-lin/receiver'
RUNTIME = 'zulu8.96.0.205-ca-fx-jdk8.0.504-win_x64'

def fetch(name, url, digest):
    target = DEST / name
    if not target.exists() or hashlib.sha256(target.read_bytes()).hexdigest() != digest:
        print('Downloading', name, flush=True)
        partial = target.with_suffix('.partial')
        urllib.request.urlretrieve(url, partial)
        if hashlib.sha256(partial.read_bytes()).hexdigest() != digest:
            partial.unlink()
            raise RuntimeError('SHA-256 mismatch: ' + name)
        partial.replace(target)
    return target

def windows(path):
    if os.name == 'nt':
        return str(path.resolve())
    return subprocess.check_output(['wslpath', '-w', str(path.resolve())], text=True).strip()

def main():
    DEST.mkdir(parents=True, exist_ok=True)
    library = fetch('LIN_Library_API_Demo.zip',
        'https://ww1.microchip.com/downloads/aemDocuments/documents/OTH/ApplicationNotes/AppnoteSourceCode/LIN_Library_API_Demo.zip',
        'd391002a0d34b644c7bf778bb8f4740f30adb5189bb1ab573c3e92a019cd02cb')
    runtime = fetch('runtime.zip', 'https://cdn.azul.com/zulu/bin/' + RUNTIME + '.zip',
        'fea91cc21ed8a8a874bb3f9cdb69d9c7a136f729449a41ea409671edd7739f36')
    lib = DEST / 'lib'
    lib.mkdir(exist_ok=True)
    with zipfile.ZipFile(library) as archive:
        for name in archive.namelist():
            if '/lib/' in name and name.endswith('.jar'):
                (lib / Path(name).name).write_bytes(archive.read(name))
            elif name.lower().endswith('/readme.txt'):
                (DEST / 'MICROCHIP-NOTICE.txt').write_bytes(archive.read(name))
    java_bin = DEST / 'runtime' / RUNTIME / 'bin'
    if not (java_bin / 'javac.exe').exists():
        with zipfile.ZipFile(runtime) as archive:
            archive.extractall(DEST / 'runtime')
    if os.name != 'nt':
        for exe in java_bin.glob('*.exe'):
            exe.chmod(exe.stat().st_mode | 0o111)
    classes = DEST / 'classes'
    classes.mkdir(exist_ok=True)
    cp = ';'.join(windows(jar) for jar in sorted(lib.glob('*.jar')))
    subprocess.run([str(java_bin / 'javac.exe'), '-proc:none', '-cp', cp,
        '-d', windows(classes), windows(ROOT / 'lin/MicrochipLinReceiver.java')], check=True)
    print('LIN receiver ready. No drivers or system settings changed.')

if __name__ == '__main__':
    main()
