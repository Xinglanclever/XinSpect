import re

print('input     [A-Z]+   [A-Z]+[0-9]?')
for s in ['A', 'ABC', 'A1', 'ABC1', '1', '12', 'F10', 'SEC', 'TPM']:
    a = bool(re.fullmatch(r'[A-Z]+', s))
    b = bool(re.fullmatch(r'[A-Z]+[0-9]?', s))
    print(f'{s!r:<10}{a!s:<10}{b}')

print()
print('same, but with the class written out longhand:')
for s in ['A', 'ABC', 'A1', 'F10']:
    a = bool(re.fullmatch(r'[ABCDEFGHIJKLMNOPQRSTUVWXYZ]+', s))
    print(f'  {s!r:<8} -> {a}')
