"""Compare actual packaged-process receipts; reject missing stages and any progress rollback."""
import hashlib
import json
from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve()
checks = []
def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))
def check(value, name, detail=None):
    checks.append({'name': name, 'passed': bool(value), 'detail': detail})
    if not value:
        raise AssertionError(name)
def stable(tables):
    # Opening and closing a book legitimately updates its last-read timestamp.
    return {table: [row[:6] if table == 'sessions' else row for row in rows] for table, rows in tables.items()}
failure = None
try:
    regression = read(root / 'regression/report.json')
    check(regression['passed'], 'full reading regression passed', len(regression['checks']))
    for scenario in ('installation', 'migration'):
        reports = [read(root / scenario / (stage + '.json')) for stage in ('first','restart','upgrade','reinstall')]
        for report, version in zip(reports, ('1.0.9.0','1.0.9.0','1.0.10.0','1.0.10.0')):
            check(report['passed'], scenario + ': ' + report['stage'] + ' passed')
            check(report['package'].startswith('Qidu.InstallValidation_' + version + '_'), scenario + ': installed package identity ' + report['stage'], report['package'])
            check('\\WindowsApps\\' in report['executable'], scenario + ': executable came from installed package')
        if scenario == 'installation':
            check(all(not rows for rows in reports[0]['before'].values()), 'fresh install starts without legacy data')
            check(len(reports[0]['after']['books']) == 2 and len(reports[0]['after']['bookmarks']) == 1, 'new imports and bookmark created inside packaged app')
        else:
            baseline = read(root / scenario / 'baseline.json')
            prepared = read(root / scenario / 'native-prepare.json')
            check(prepared['before'] == baseline['tables'], 'native migration copied all rows without altering original state')
            check(stable(reports[0]['before']) == stable(baseline['tables']), 'packaged app resumes all migrated book/session/bookmark/settings data')
            check(len(reports[0]['after']['books']) == len(baseline['tables']['books']) + 1, 'new book added after migration')
        first = reports[0]['after']
        for report in reports[1:]:
            check(stable(report['before']) == stable(first), scenario + ': progress, fonts, shelf, bookmarks and settings survive ' + report['stage'])
            check(stable(report['after']) == stable(first), scenario + ': mode/font verification leaves state unchanged ' + report['stage'])
        library = Path(reports[-1]['library'])
        for row in first['books']:
            check(Path(row[2].replace('$LIBRARY', str(library))).is_file(), scenario + ': managed book exists after reinstall', row[1])
    removed = read(root / 'installation/removed.json')
    reinstalled = read(root / 'installation/reinstalled.json')
    cleaned = read(root / 'installation/cleaned.json')
    check(len(removed['packages']) == 0, 'Windows confirms package removed before reinstall')
    check(len(reinstalled['packages']) == 1, 'Windows confirms package reinstalled')
    check(len(cleaned['packages']) == 0, 'validation package removed after final test')
except Exception as error:
    failure = str(error)
result = {'passed': failure is None, 'checks': checks, 'failure': failure,
          'scope': 'Local unsigned MSIX lifecycle; actual Store download/signing remains pending certification.',
          'migration': 'Unpackaged application prepares shared library; MSIX resumes it. Direct legacy AppData visibility is not assumed.'}
(root / 'lifecycle-validation.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'passed': result['passed'], 'checks': len(checks), 'failure': failure}, ensure_ascii=False))
sys.exit(0 if result['passed'] else 1)
