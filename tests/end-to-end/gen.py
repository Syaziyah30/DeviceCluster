# -*- coding: utf-8 -*-
"""Generate a designed end-to-end test project.

Random device IDs would only prove the pipeline runs. These are chosen so that
each block has a predictable outcome, which is what makes the run checkable.
"""
import csv, io, os, collections

REF = r'C:\Users\sitisyaziyah\source\repos\DeviceCluster\Reference\initial_dictionary.csv'
OUT = os.path.dirname(os.path.abspath(__file__))
PROJECT, CUSTOMER = 'A9997', 'OILTEK'

# prefix -> type, last row winning, exactly as initial_map is built
mapping = {}
dupes = collections.defaultdict(list)
with io.open(REF, encoding='utf-8-sig') as fh:
    for row in csv.DictReader(fh):
        dtype, prefix = row['data_type'].strip(), row['Initial'].strip()
        if prefix in mapping and mapping[prefix] != dtype:
            dupes[prefix].append(mapping[prefix])
        mapping[prefix] = dtype
        dupes[prefix].append(dtype)

known = [p for p in sorted(mapping) if len(p) <= 4]

rows = []          # (device_id, block, expectation)


def add(dev, block, expect):
    rows.append((dev, block, expect))


# ── A. known prefixes: should classify at confidence 1.00 ───────────────────
for i, p in enumerate(known[:60]):
    add('%s%03d' % (p, 101 + i), 'A. Known prefix', 'type = %s, confidence 1.00' % mapping[p])

# ── B. unknown prefixes: should become UNKNOWN, not a guess ─────────────────
for i, p in enumerate(['ZZQ', 'QXX', 'YYK', 'WWJ', 'VVN', 'UUB', 'TTG', 'SSD']):
    add('%s%03d' % (p, 201 + i), 'B. Unknown prefix', 'UNKNOWN, 0.00 -> review queue')

# ── C. case variants: the documented C# case-sensitivity limitation ─────────
for p in ['HT', 'PT', 'DV', 'FT']:
    add('%s301' % p, 'C. Case variant', 'same type as its lower-case twin')
    add('%s301' % p.lower(), 'C. Case variant', 'C# treats this as a SECOND device')

# ── D. the ambiguous prefix already in the dictionary ──────────────────────
for i in range(4):
    add('A%03d' % (401 + i), 'D. Ambiguous prefix A',
        'whichever of %s was loaded last' % ' / '.join(sorted(set(dupes['A']))))

# ── E. formatting: cleaning must not lose or mangle these ──────────────────
add('V001.21', 'E. Formatting', 'dot preserved; prefix V')
add('  HT501  ', 'E. Formatting', 'trimmed, classified as Heater')
add('PT-502', 'E. Formatting', 'separator normalised')
add('LL_503', 'E. Formatting', 'separator normalised')

# ── write the insert script ────────────────────────────────────────────────
sql = [
    '-- End-to-end test project for the DeviceIdentifier pipeline.',
    '-- Generated - do not hand-edit. Rerun gen.py to change the set.',
    '--',
    '-- %d devices, chosen so every block has a predictable outcome.' % len(rows),
    '-- Column names match what PythonSQL reads: ProjectCode, CustomerCode, DataIds.',
    '-- If dbo.DummyTestingData has other NOT NULL columns, add them here.',
    '',
    'DELETE FROM dbo.DummyTestingData WHERE ProjectCode = %r;' % PROJECT,
    '',
    'INSERT INTO dbo.DummyTestingData (ProjectCode, CustomerCode, DataIds) VALUES',
]
vals = []
for dev, block, expect in rows:
    vals.append(("    (%-9s, %-9s, %-14s)" % ("'%s'" % PROJECT, "'%s'" % CUSTOMER,
                                              "'%s'" % dev.replace("'", "''")), block))
# The separator has to go before the comment, or it ends up commented out.
body = []
for i, (v, block) in enumerate(vals):
    body.append('%s%s  -- %s' % (v, ',' if i < len(vals) - 1 else ';', block))
sql.append('\n'.join(body))
sql.append('')
sql.append('SELECT COUNT(*) AS Inserted FROM dbo.DummyTestingData WHERE ProjectCode = %r;' % PROJECT)

io.open(os.path.join(OUT, 'test-project-A9997.sql'), 'w', encoding='utf-8', newline='\r\n').write('\n'.join(sql).replace("'", "'"))

# ── expectations table, for checking the run afterwards ────────────────────
exp = ['device_id,block,expected']
for dev, block, expect in rows:
    exp.append('%s,%s,%s' % (dev.strip(), block, expect))
io.open(os.path.join(OUT, 'expected-A9997.csv'), 'w', encoding='utf-8', newline='').write('\n'.join(exp))

counts = collections.Counter(b for _, b, _ in rows)
print('generated %d devices for project %s' % (len(rows), PROJECT))
for b, n in sorted(counts.items()):
    print('  %-26s %d' % (b, n))
print()
print('ambiguous prefix A maps to: %s' % ' / '.join(sorted(set(dupes['A']))))
