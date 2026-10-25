"""Read-only SQLite backup of the current preview library into explicitly isolated QA roots."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sqlite3
import sys
import uuid

report = Path(sys.argv[1]).resolve()
report.mkdir(parents=True, exist_ok=False)
run = uuid.uuid4().hex
source = Path(os.environ['LOCALAPPDATA']) / 'TxtReaderPreview'
legacy = Path(os.environ['LOCALAPPDATA']) / 'Qidu-Validation' / run / 'legacy'
target = Path(os.environ['USERPROFILE']) / 'Qidu-Validation' / run / 'library'
legacy.mkdir(parents=True, exist_ok=False)
with sqlite3.connect((source / 'reading.db').as_uri() + '?mode=ro', uri=True) as original:
    with sqlite3.connect(legacy / 'reading.db') as clone:
        original.backup(clone)
        paths = {row[0] for table in ('books', 'sessions') for row in clone.execute(f'SELECT path FROM {table}')}
        copied = []
        for text in paths:
            path = Path(text)
            if path.is_relative_to(source):
                relative = path.relative_to(source)
                destination = legacy / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(path, destination)
                digest = hashlib.sha256(destination.read_bytes()).hexdigest()
                assert digest == hashlib.sha256(path.read_bytes()).hexdigest()
                copied.append({'relative': str(relative), 'sha256': digest})
                for table in ('books', 'sessions'):
                    clone.execute(f'UPDATE {table} SET path=? WHERE path=?', (str(destination), text))
        clone.commit()
        tables = {table: [[value.replace(str(legacy), '$LIBRARY') if isinstance(value, str) else value for value in row]
                          for row in clone.execute(f'SELECT * FROM {table} ORDER BY 1')]
                  for table in ('books', 'sessions', 'bookmarks', 'settings')}
        assert clone.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
baseline = {'runId': run, 'source': str(source), 'legacy': str(legacy), 'target': str(target), 'tables': tables, 'files': copied}
(report / 'baseline.json').write_text(json.dumps(baseline, ensure_ascii=False, indent=2), encoding='utf-8')
for stage, advance in [('first', True), ('restart', False), ('upgrade', False), ('reinstall', False)]:
    (report / f'{stage}-config.json').write_text(json.dumps({'RunId': run, 'Stage': stage, 'Advance': advance,
        'ReportFolder': str(report)}, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'runId': run, 'books': len(tables['books']), 'bookmarks': len(tables['bookmarks']), 'copied': len(copied)}, ensure_ascii=False))
