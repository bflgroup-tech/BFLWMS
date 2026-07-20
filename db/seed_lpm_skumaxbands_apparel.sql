/*
 * Seed for dbo.LPM_SkuMaxBands — Apparel data from operator's screenshot.
 *
 * IMPORTANT: Fill in @Apparel below with the actual DivCode value for
 * Apparel before running. Example: if DivCode 410 = Apparel, set @Apparel = 410.
 *
 * The last band per Grade is PoQtyFrom=1001, PoQtyTo=999999 so "above 1001"
 * uses the same tier ceilings as the 1001-1500 range (per user spec).
 *
 * Re-runs are idempotent: existing rows for the same DivCode are DELETEd
 * before the INSERT so the seed can be updated by editing this file and
 * re-running.
 */

DECLARE @Apparel INT = 0;   -- <-- REPLACE 0 with the DivCode for Apparel

IF @Apparel = 0
BEGIN
    RAISERROR('Set @Apparel to the DivCode for Apparel before running this seed.', 16, 1);
    RETURN;
END;

BEGIN TRAN;

DELETE FROM dbo.LPM_SkuMaxBands WHERE DivCode = @Apparel;

INSERT dbo.LPM_SkuMaxBands (DivCode, VolumeGroup, NoOfStores, PoQtyFrom, PoQtyTo, MinMin, MinMax, IdealMax, MaxMax) VALUES
  -- Grade A (2 stores)
  (@Apparel, 'A',  2,    0,    125,   1,   2,   3,   4),
  (@Apparel, 'A',  2,  125,    250,   2,   4,   6,   8),
  (@Apparel, 'A',  2,  251,    500,   4,   8,  12,  16),
  (@Apparel, 'A',  2,  501,   1000,   8,  16,  24,  32),
  (@Apparel, 'A',  2, 1001, 999999,  12,  24,  36,  48),

  -- Grade B (1 store)
  (@Apparel, 'B',  1,    0,    125,   1,   2,   3,   4),
  (@Apparel, 'B',  1,  125,    250,   2,   4,   5,   7),
  (@Apparel, 'B',  1,  251,    500,   3,   7,  10,  14),
  (@Apparel, 'B',  1,  501,   1000,   6,  14,  20,  28),
  (@Apparel, 'B',  1, 1001, 999999,   9,  21,  30,  42),

  -- Grade C (3 stores)
  (@Apparel, 'C',  3,    0,    125,   1,   2,   3,   3),
  (@Apparel, 'C',  3,  125,    250,   2,   3,   4,   6),
  (@Apparel, 'C',  3,  251,    500,   3,   6,   8,  12),
  (@Apparel, 'C',  3,  501,   1000,   6,  12,  16,  24),
  (@Apparel, 'C',  3, 1001, 999999,   9,  18,  24,  36),

  -- Grade D (12 stores)
  (@Apparel, 'D', 12,    0,    125,   1,   2,   2,   3),
  (@Apparel, 'D', 12,  125,    250,   2,   3,   4,   5),
  (@Apparel, 'D', 12,  251,    500,   3,   5,   8,  10),
  (@Apparel, 'D', 12,  501,   1000,   5,  10,  16,  20),
  (@Apparel, 'D', 12, 1001, 999999,   9,  18,  24,  30),

  -- Grade E (22 stores)
  (@Apparel, 'E', 22,    0,    125,   1,   1,   2,   2),
  (@Apparel, 'E', 22,  125,    250,   1,   2,   4,   4),
  (@Apparel, 'E', 22,  251,    500,   2,   4,   6,   8),
  (@Apparel, 'E', 22,  501,   1000,   4,   8,  12,  16),
  (@Apparel, 'E', 22, 1001, 999999,   6,  12,  18,  24),

  -- Grade F (9 stores)
  (@Apparel, 'F',  9,    0,    125,   1,   1,   1,   2),
  (@Apparel, 'F',  9,  125,    250,   1,   1,   2,   3),
  (@Apparel, 'F',  9,  251,    500,   1,   2,   4,   6),
  (@Apparel, 'F',  9,  501,   1000,   2,   4,   8,  12),
  (@Apparel, 'F',  9, 1001, 999999,   3,   6,  12,  18),

  -- Grade G (26 stores)
  (@Apparel, 'G', 26,    0,    125,   1,   1,   1,   1),
  (@Apparel, 'G', 26,  125,    250,   1,   1,   2,   2),
  (@Apparel, 'G', 26,  251,    500,   1,   2,   3,   4),
  (@Apparel, 'G', 26,  501,   1000,   2,   4,   6,   8),
  (@Apparel, 'G', 26, 1001, 999999,   3,   6,  12,  12),

  -- Grade H (30 stores)
  (@Apparel, 'H', 30,    0,    125,   1,   1,   1,   1),
  (@Apparel, 'H', 30,  125,    250,   1,   1,   2,   2),
  (@Apparel, 'H', 30,  251,    500,   1,   2,   2,   3),
  (@Apparel, 'H', 30,  501,   1000,   2,   4,   4,   8),
  (@Apparel, 'H', 30, 1001, 999999,   3,   6,   9,  12),

  -- Grade I (11 stores)
  (@Apparel, 'I', 11,    0,    125,   0,   1,   1,   1),
  (@Apparel, 'I', 11,  125,    250,   1,   1,   1,   1),
  (@Apparel, 'I', 11,  251,    500,   1,   1,   1,   2),
  (@Apparel, 'I', 11,  501,   1000,   2,   2,   2,   4),
  (@Apparel, 'I', 11, 1001, 999999,   4,   4,   6,   6);

COMMIT TRAN;
