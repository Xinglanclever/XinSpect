import re

print('== the exact patterns used by the audit scripts ==')
pats = {
    'ultimate_audit.py ROW': r'^\|\s*(★?)\s*(P(\d{1,2})-(\d{3}))\s*\|(.*)$',
    'ultimate_check.py ROW': r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|',
    'domain code':           r'[A-Z]+\d?',
    'domain code braces':    r'[A-Z]+[0-9]{1,2}[0-9]?',
}
lines = ['| P1-002 | x |', '| ★P1-001 | x |', '| F10 | y | 10 |']
for name, p in pats.items():
    rx = re.compile(p)
    print(f'-- {name}: {p!r}')
    for l in lines:
        print('     ', repr(l[:22]), '->', bool(rx.match(l) if '^' in p else rx.fullmatch(l)))
    if name == 'domain code':
        for s in ['F1', 'F10', 'AC', 'SEC', 'TPM']:
            print('      fullmatch', s, '->', bool(rx.fullmatch(s)))

print()
print('== does re.RegexFlag / locale change anything? ==')
print('ASCII:', bool(re.fullmatch(r'[A-Z]+', 'A1', re.ASCII)))
print('compile then fullmatch:', bool(re.compile(r'[A-Z]+').fullmatch('A1')))
print('pattern object search  :', re.compile(r'[A-Z]+').search('A1'))
