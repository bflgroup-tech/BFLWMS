/*
 * Re-orders dbo.LPM_VolumeGroupRange.SortOrder so ECOM (Z) has priority 4
 * per operator spec (was 99):
 *
 *   A=1, B=2, C=3, Z=4, D=5, E=6, F=7, G=8, H=9, I=10
 *
 * SortOrder doubles as the Fill SKUMAX + RR allocation priority (lower =
 * higher priority within a store x div item pick). Editable in-place if
 * the ordering needs to change again later.
 *
 * Idempotent.
 */
;WITH src (VolumeGroup, SortOrder) AS (
    SELECT * FROM (VALUES
        ('A',  1),
        ('B',  2),
        ('C',  3),
        ('Z',  4),
        ('D',  5),
        ('E',  6),
        ('F',  7),
        ('G',  8),
        ('H',  9),
        ('I', 10)
    ) v (VolumeGroup, SortOrder)
)
UPDATE dst
   SET dst.SortOrder = src.SortOrder,
       dst.UpdatedTS = DATEADD(hour, 4, SYSUTCDATETIME())
  FROM dbo.LPM_VolumeGroupRange AS dst
  JOIN src ON dst.VolumeGroup = src.VolumeGroup
 WHERE dst.SortOrder <> src.SortOrder;
