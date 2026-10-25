#!/usr/bin/env python3
"""Create an Apple Silicon preview bundle; preserve executable modes in its ZIP."""
import argparse
import hashlib
import json
import os
import plistlib
from pathlib import Path
import shutil
import stat
import struct
import subprocess
import sys
import zipfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--publish', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
parser.add_argument('--report', required=True, type=Path)
parser.add_argument('--version', default='0.2.0-preview.2')
args = parser.parse_args()
report = json.loads(args.report.read_text(encoding='utf-8-sig'))
if not report.get('passed') or len(report.get('checks', [])) < 50:
    raise SystemExit('A passing desktop reading report is required.')
publish = args.publish.resolve()
out = args.output.resolve()
if out.exists():
    raise SystemExit('Use a new output directory; existing previews are never overwritten.')
host = publish / 'Qidu'
data = host.read_bytes()
if data[:4] != b'\xcf\xfa\xed\xfe' or struct.unpack_from('<I', data, 4)[0] != 0x100000c:
    raise SystemExit('Expected an arm64 Mach-O app host.')
if not (publish / 'libAvaloniaNative.dylib').is_file() or not (publish / 'libe_sqlite3.dylib').is_file():
    raise SystemExit('Missing native Mac dependencies.')
out.mkdir(parents=True)
app = out / '栖读.app'
contents = app / 'Contents'
macos = contents / 'MacOS'
resources = contents / 'Resources'
resources.mkdir(parents=True)
shutil.copytree(publish, macos, ignore=shutil.ignore_patterns('*.pdb'))
shutil.copy2(root / 'LICENSE', resources / 'LICENSE.txt')
shutil.copytree(root / 'third-party', resources / 'third-party')
shutil.copy2(root / 'docs' / 'macos-preview.txt', out / '使用说明.txt')
# The existing 512px project icon fits the modern PNG-backed ic09 representation.
png = (root / 'src/TxtReader.App/Assets/AppIcon.png').read_bytes()
if png[:8] != b'\x89PNG\r\n\x1a\n' or struct.unpack('>II', png[16:24]) != (512, 512):
    raise SystemExit('Expected the existing 512px project icon.')
icon = b'ic09' + struct.pack('>I', len(png) + 8) + png
(resources / 'Qidu.icns').write_bytes(b'icns' + struct.pack('>I', len(icon) + 8) + icon)
plist = dict(CFBundleIdentifier='io.github.ray0766.qidu', CFBundleName='栖读',
             CFBundleDisplayName='栖读', CFBundleExecutable='Qidu', CFBundleIconFile='Qidu.icns',
             CFBundlePackageType='APPL', CFBundleInfoDictionaryVersion='6.0',
             CFBundleShortVersionString='0.2.0', CFBundleVersion='20002',
             LSMinimumSystemVersion='15.0', NSHighResolutionCapable=True,
             NSPrincipalClass='NSApplication', LSApplicationCategoryType='public.app-category.books',
             NSHumanReadableCopyright='Copyright Ray0766. MIT License.')
with (contents / 'Info.plist').open('wb') as stream:
    plistlib.dump(plist, stream)
for file in macos.rglob('*'):
    if file.is_file():
        file.chmod(0o755 if file.name in ('Qidu', 'createdump') or file.suffix == '.dylib' else 0o644)
# Ad-hoc signing verifies a local development bundle, not publisher identity.
# A normal trusted public release still requires Developer ID and notarization.
signing = 'cross-built; SDK app-host ad-hoc signature; bundle not signed'
if sys.platform == 'darwin':
    subprocess.run(['codesign', '--force', '--deep', '--sign', '-', '--entitlements',
                    str(root / 'installer/macos/preview.entitlements'), str(app)], check=True)
    subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)
    signing = 'ad-hoc development signature verified; not notarized'
zip_path = out / f'Qidu-{args.version}-macOS-arm64.zip'
files = [file for file in app.rglob('*') if file.is_file()] + [out / '使用说明.txt']
for file in files:
    relative = file.relative_to(out)
    if file.suffix.lower() in ('.epub', '.db', '.pfx', '.p12') or 'QiduLibrary' in str(relative):
        raise SystemExit(f'Private data is forbidden in a package: {relative}')
with zipfile.ZipFile(zip_path, 'x', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for file in sorted(files):
        entry = zipfile.ZipInfo(file.relative_to(out).as_posix())
        entry.create_system = 3
        executable = file.name in ('Qidu', 'createdump') or file.suffix == '.dylib'
        entry.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
        entry.compress_type = zipfile.ZIP_DEFLATED
        archive.writestr(entry, file.read_bytes())
with zipfile.ZipFile(zip_path) as archive:
    if archive.testzip() is not None:
        raise SystemExit('ZIP integrity verification failed.')
    entry = archive.getinfo('栖读.app/Contents/MacOS/Qidu')
    if (entry.external_attr >> 16) & 0o111 != 0o111:
        raise SystemExit('The app host lost executable permissions.')
digest = hashlib.sha256(zip_path.read_bytes()).hexdigest().upper()
checksum = out / f'SHA256-{args.version}-macOS-arm64.txt'
checksum.write_text(f'{digest}  {zip_path.name}\n', encoding='ascii')
receipt = dict(version=args.version, architecture='arm64', minimum_macos='15.0',
               native_mac_verified=report.get('nativeMacVerified', False), signing=signing,
               files=len(files), package=zip_path.name, bytes=zip_path.stat().st_size, sha256=digest)
(out / 'package-receipt.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(receipt, ensure_ascii=False, indent=2))
