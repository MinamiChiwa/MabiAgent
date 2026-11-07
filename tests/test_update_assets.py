"""Release asset limits, legacy coverage and portable asset ordering."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

GENERATOR = Path(__file__).resolve().parents[1] / 'tools/build_update_assets.py'


class UpdateAssetTests(unittest.TestCase):
    def test_groups_common_files_but_keeps_union_of_legacy_changes_direct(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root/'package'
            package.mkdir()
            rows = []
            for name, value in [('common.dll', b'common'), ('one.dll', b'one'), ('two.dll', b'two')]:
                (package/name).write_bytes(value)
                rows.append(dict(path=name, bytes=len(value), sha256=hashlib.sha256(value).hexdigest()))
            (package/'release-manifest.json').write_text(json.dumps(dict(files=rows)), encoding='utf-8')
            for index, changed in enumerate(['one.dll', 'two.dll']):
                baseline = [dict(r, sha256='a'*64) if r['path']==changed else r for r in rows]
                (root/f'old{index}.json').write_text(json.dumps(dict(files=baseline)), encoding='utf-8')
            output = root/'assets'
            command = [sys.executable, '-B', str(GENERATOR), '--package', str(package), '--version', '0.6.1', '--output', str(output)]
            for index in range(2): command += ['--legacy-baseline', str(root/f'old{index}.json')]
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr.decode())
            manifest = json.loads((output/'update-manifest.json').read_text(encoding='utf-8'))
            by_path = {r['path']: r for r in manifest['files']}
            self.assertIn('pack', by_path['common.dll'])
            for name in ['one.dll', 'two.dll', 'release-manifest.json']:
                self.assertNotIn('pack', by_path[name])
                self.assertTrue((output/by_path[name]['blob']).is_file())
            with zipfile.ZipFile(output/by_path['common.dll']['pack']) as archive:
                self.assertEqual(b'common', archive.read(by_path['common.dll']['blob']))
            names = [p.name for p in output.iterdir()] + ['MabiAgent-0.6.1-win-x64.zip', 'SHA256SUMS.txt']
            self.assertEqual('MabiAgent-0.6.1-win-x64.zip', sorted(names, key=str.casefold)[0])
            self.assertTrue(all(n.startswith('z-update-') for n in names if n.endswith(('.bin',)) or n.startswith('z-update-pack-')))

    def test_excess_assets_fail_before_writing_an_incomplete_release(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = root/'package'
            package.mkdir()
            rows = []
            for i in range(1000):
                value = str(i).encode()
                (package/f'{i}.txt').write_bytes(value)
                rows.append(dict(path=f'{i}.txt', bytes=len(value), sha256=hashlib.sha256(value).hexdigest()))
            (package/'release-manifest.json').write_text(json.dumps(dict(files=rows)), encoding='utf-8')
            output = root/'assets'
            result = subprocess.run([sys.executable, '-B', str(GENERATOR), '--package', str(package), '--version', '0.6.1', '--output', str(output)], capture_output=True)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual([], list(output.iterdir()))


if __name__ == '__main__': unittest.main()
