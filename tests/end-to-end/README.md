# End-to-end test — project A9997

A designed test of the whole chain: SQL → ML service → quota allocation → SQL.

84 devices, chosen so that **every block has a predictable outcome**. A hundred
random device IDs would only prove the pipeline ran; these prove it behaved.

| Block | Devices | What it proves |
|---|---|---|
| A. Known prefix | 60 | Real prefixes classify at confidence 1.00 |
| B. Unknown prefix | 8 | Unrecognised prefixes return UNKNOWN — the system does not guess |
| C. Case variant | 8 | `HT301` and `ht301`: Python normalises, C# does not (see step 5) |
| D. Ambiguous prefix `A` | 4 | One prefix maps to one type only — last definition wins |
| E. Formatting | 4 | `V001.21` keeps its dot; spaces, `-` and `_` are handled |

---

## Before you start

The prediction half is already verified against the live service — all 84 IDs
were sent to `http://128.100.8.213:8000/predict/device-type` and every block
behaved as the table above says. What this test adds is the **C# half**: quota
allocation, the floating split, and the two SQL writes.

---

## 1. Load the test project

Run `test-project-A9997.sql` against `XenCreator`.

It deletes any existing `A9997` rows first, so it is safe to re-run. If
`dbo.DummyTestingData` has NOT NULL columns beyond `ProjectCode`,
`CustomerCode` and `DataIds`, add them to the INSERT before running.

Expect `Inserted = 84`.

## 2. Run the pipeline

```
run-service-app.cmd A9997 --unattended
```

Note the four numbers it prints: assigned, unknown, unallocated, cluster groups.

## 3. Everything is accounted for

```sql
SELECT
  (SELECT COUNT(*) FROM dbo.OutputDeviceAssignment
     WHERE ProjectCode = 'A9997')                                    AS Assigned,
  (SELECT COUNT(*) FROM dbo.DeviceReviewQueue
     WHERE ProjectCode = 'A9997' AND Category = 'UnknownPrediction') AS UnknownPrediction,
  (SELECT COUNT(*) FROM dbo.DeviceReviewQueue
     WHERE ProjectCode = 'A9997' AND Category = 'Unallocated')       AS Unallocated;
```

**The three must sum to 84.** A device that goes missing is the failure this
catches — it means something was dropped silently between the stages.

Expect most devices in `Unallocated`: real quota patterns exist for Section 2
only, so there is nowhere to put the rest. That is missing reference data, not
a fault.

## 4. No device is in both tables at once

```sql
SELECT a.DeviceId
FROM dbo.OutputDeviceAssignment a
JOIN dbo.DeviceReviewQueue q
  ON q.DeviceId = a.DeviceId AND q.ProjectCode = a.ProjectCode
WHERE a.ProjectCode = 'A9997';
```

**Must return 0 rows.** This is the cross-table reconciliation working.

## 5. The unknown prefixes went to review, not to a guess

```sql
SELECT DeviceId, Category, DeviceType
FROM dbo.DeviceReviewQueue
WHERE ProjectCode = 'A9997'
  AND DeviceId IN ('ZZQ201','QXX202','YYK203','WWJ204',
                   'VVN205','UUB206','TTG207','SSD208')
ORDER BY DeviceId;
```

**Expect all 8, Category = `UnknownPrediction`.** If any is missing, or carries
a real device type, the system guessed at something it should have deferred.

## 6. Re-running is safe

Run step 2 again, unchanged, then:

```sql
SELECT DeviceId, COUNT(*) AS Copies
FROM dbo.OutputDeviceAssignment
WHERE ProjectCode = 'A9997'
GROUP BY DeviceId
HAVING COUNT(*) > 1;
```

**Must return 0 rows**, and the four printed numbers must be identical to the
first run. This is the `MERGE` upsert working — rows update in place instead of
duplicating.

## 7. The case-sensitivity limitation, demonstrated

```sql
SELECT DeviceId FROM dbo.OutputDeviceAssignment
WHERE ProjectCode = 'A9997' AND UPPER(DeviceId) IN ('HT301','PT301','DV301','FT301')
UNION ALL
SELECT DeviceId FROM dbo.DeviceReviewQueue
WHERE ProjectCode = 'A9997' AND UPPER(DeviceId) IN ('HT301','PT301','DV301','FT301')
ORDER BY DeviceId;
```

**Expect 8 rows — both cases of each of the four.** That is not a test failure:
it is the known limitation recorded in `HANDOFF.md`. Python classifies `HT301`
and `ht301` identically, but the C# side compares with the default ordinal
comparer, so they are two devices all the way through.

This query is the reproduction case. When someone fixes the comparer, it should
return 4 rows instead of 8.

---

## Cleaning up

```sql
DELETE FROM dbo.DummyTestingData        WHERE ProjectCode = 'A9997';
DELETE FROM dbo.OutputDeviceAssignment  WHERE ProjectCode = 'A9997';
DELETE FROM dbo.DeviceReviewQueue       WHERE ProjectCode = 'A9997';
```

## Regenerating

`gen.py` builds `test-project-A9997.sql` and `expected-A9997.csv` from
`Reference/initial_dictionary.csv`, so the known-prefix block always reflects
the dictionary actually in use.

## What this test does not prove

**Accuracy.** There is no ground truth here — no engineer has said which cluster
each device belongs in. This proves the pipeline is correct and repeatable, not
that its predictions are right. A validated accuracy figure needs a run against
a completed real project, which is still an open item.
