"""Freeze the product SOURCE of a candidate as a SHA-256 manifest.

The worktree is intentionally dirty and most of Civil Delivery is untracked, so a
git SHA alone cannot identify the source of a frozen binary. This manifest records
every product/test/installer/profile source file with its hash and one root hash,
next to the release metadata, so a reviewer can bind Setup → staged DLLs → source.

Usage:
  python release/build-source-manifest.py [version]
  python release/build-source-manifest.py [version] --verify

The verify form is deliberately read-only and fails on every missing, extra or
changed source file.  Release gates call it before trusting a staged binary.
"""
import hashlib, json, pathlib, sys, datetime

repo = pathlib.Path(__file__).resolve().parent.parent
release = json.loads((repo / 'release' / 'release.json').read_text(encoding='utf-8-sig'))
args = [arg for arg in sys.argv[1:] if arg != '--verify']
if len(args) > 1:
    raise SystemExit('usage: build-source-manifest.py [version] [--verify]')
verify = '--verify' in sys.argv[1:]
version = args[0] if args else release['package_revision']

ROOTS = [
    'MahodAI.CivilDelivery.Core', 'MahodAI.Civil3D.Plugin', 'MahodAI.Core.Tests',
    'MahodAI.Civil3D.Plugin.Tests', 'MahodAI.SaveGuidance.Tests',
    'fixtures', 'profiles', 'installer', 'release', 'runtime-gate',
    'nataly',
    'MahodAI.bundle/PackageContents.xml', 'Directory.Build.props', 'global.json', 'MahodAI.Civil3D.Plugin.sln',
]
# 'tmp' holds test scratch (codex-tests-*, TRX) and is never product source; any stage_<v> is a build output.
SKIP_DIRS = {'bin', 'obj', 'bin2026', 'out', 'stage', 'publish', 'tmp', 'TestResults'}
EXT = {'.cs', '.xaml', '.csproj', '.props', '.targets', '.sln', '.yaml', '.yml', '.xml', '.ps1',
       '.py', '.json', '.manifest', '.dwg', '.md', '.txt', '.resx', '.xlsx', '.png',
       '.html', '.jpg', '.jpeg', '.webp'}

entries = {}
for root in ROOTS:
    p = repo / root
    if p.is_file():
        entries[root] = hashlib.sha256(p.read_bytes()).hexdigest()
        continue
    for f in sorted(p.rglob('*')):
        if not f.is_file() or f.suffix.lower() not in EXT:
            continue
        rel = f.relative_to(repo).as_posix()
        if any(part in SKIP_DIRS or part.startswith('stage_') for part in f.relative_to(repo).parts):
            continue
        # These files are intentionally updated only AFTER the exact binaries pass
        # their live Civil gate.  Binding them into the pre-live binary-source freeze
        # creates an impossible cycle: PENDING -> ACCEPTED or adding the accepted
        # screenshots would invalidate the source manifest, forcing a new untested
        # installer.  Their final bytes are instead hash-bound by the live attestation,
        # guide build and two-file employee-package manifest.
        if (rel == 'release/release.json' or
                rel.startswith('docs/civil-delivery/') or
                rel.startswith('nataly/docs/') or
                rel.startswith('nataly/guide/') or
                (rel.startswith('runtime-gate/') and f.suffix.lower() == '.md')):
            continue
        if rel.startswith('release/source_manifest_'):
            continue
        entries[rel] = hashlib.sha256(f.read_bytes()).hexdigest()

root_hash = hashlib.sha256('\n'.join(f'{k}  {v}' for k, v in sorted(entries.items())).encode('utf-8')).hexdigest()
manifest = {
    'schema_version': 1,
    'package_revision': version,
    'platform_version': release['platform_version'],
    'canonical_employee_package': release['canonical_employee_package'],
    'generated_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'file_count': len(entries),
    'root_sha256': root_hash,
    'files': dict(sorted(entries.items())),
}
out = repo / 'release' / f'source_manifest_{version}.json'
if verify:
    if not out.is_file():
        raise SystemExit(f'SOURCE MANIFEST VERIFY FAILED: missing {out}')
    try:
        frozen = json.loads(out.read_text(encoding='utf-8-sig'))
    except Exception as exc:
        raise SystemExit(f'SOURCE MANIFEST VERIFY FAILED: unreadable {out}: {exc}')

    frozen_files = frozen.get('files') if isinstance(frozen.get('files'), dict) else {}
    missing = sorted(set(frozen_files) - set(entries))
    extra = sorted(set(entries) - set(frozen_files))
    changed = sorted(path for path in set(entries) & set(frozen_files)
                     if entries[path].lower() != str(frozen_files[path]).lower())
    metadata_ok = (
        frozen.get('schema_version') == 1 and
        frozen.get('package_revision') == version and
        frozen.get('platform_version') == release['platform_version'] and
        frozen.get('canonical_employee_package') == release['canonical_employee_package'] and
        frozen.get('file_count') == len(entries) and
        frozen.get('root_sha256') == root_hash
    )
    if missing or extra or changed or not metadata_ok:
        def preview(values):
            return ', '.join(values[:12]) + (' ...' if len(values) > 12 else '')
        raise SystemExit(
            'SOURCE MANIFEST VERIFY FAILED: '
            f'metadata_ok={metadata_ok} missing={len(missing)} [{preview(missing)}] '
            f'extra={len(extra)} [{preview(extra)}] '
            f'changed={len(changed)} [{preview(changed)}]')
    print(f'{out.name}: VERIFIED files={len(entries)} root_sha256={root_hash}')
else:
    out.write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding='utf-8')
    print(f'{out.name}: files={len(entries)} root_sha256={root_hash}')
