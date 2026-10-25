"""Check saved progress and hash each managed book before an MSIX lifecycle operation."""
import hashlib
import json
from pathlib import Path
import sqlite3
import sys

root = Path(sys.argv[1]).resolve()
stage = sys.argv[2]
checkpoint = sys.argv[3] if len(sys.argv) > 3 else stage
def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))
def stable(tables):
    return {table: [row[:6] if table == 'sessions' else row for row in rows] for table, rows in tables.items()}
receipts = {}
for scenario in ('installation', 'migration'):
    first = read(root / scenario / 'first.json')
    latest = read(root / scenario / (stage + '.json'))
    assert first['passed'] and latest['passed'], scenario + ': package probe failed'
    assert stable(first['after']) == stable(latest['before']), scenario + ': state changed between sessions'
    assert stable(first['after']) == stable(latest['after']), scenario + ': probe changed reading state'
    library = Path(latest['library'])
    hashes = {}
    for row in first['after']['books']:
        book = Path(row[2].replace('$LIBRARY', str(library)))
        hashes[str(book.relative_to(library))] = hashlib.sha256(book.read_bytes()).hexdigest()
    with sqlite3.connect((library / 'reading.db').as_uri() + '?mode=ro', uri=True) as connection:
        assert connection.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
        tables = {table: [[value.replace(str(library), '$LIBRARY') if isinstance(value, str) else value for value in row]
                          for row in connection.execute(f'SELECT * FROM {table} ORDER BY 1')]
                  for table in ('books', 'sessions', 'bookmarks', 'settings')}
    assert stable(tables) == stable(first['after']), scenario + ': closed database differs from reported state'
    if scenario == 'migration':
        baseline = read(root / scenario / 'baseline.json')
        prepared = read(root / scenario / 'native-prepare.json')
        assert prepared['before'] == baseline['tables'], 'native copy altered source data'
        assert stable(first['before']) == stable(baseline['tables']), 'MSIX did not resume migrated state'
    receipts[scenario] = {'library': str(library), 'hashes': hashes, 'books': len(tables['books']),
                          'bookmarks': len(tables['bookmarks']), 'sessions': tables['sessions']}
baseline_path = root / 'checkpoint-restart.json'
if stage != 'restart':
    baseline = read(baseline_path)
    for scenario in receipts:
        assert receipts[scenario]['hashes'] == baseline[scenario]['hashes'], scenario + ': book bytes changed'
(root / ('checkpoint-' + checkpoint + '.json')).write_text(json.dumps(receipts, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'passed': True, 'stage': checkpoint, 'libraries': {name: {'books': item['books'], 'bookmarks': item['bookmarks'],
    'offsets': [row[2] for row in item['sessions']]} for name, item in receipts.items()}}, ensure_ascii=False))
