/*  Make the LOWEST Volume Group band open-ended at the bottom, so stores with
    zero (or negative) AvgSalesPct still get graded instead of being written
    with a blank Grade.

    RUN ON THE ON-PREM DB (the one hosting LPM_VolumeGroupRange_Country).

    Why
    ---
    Grade lookup in OtsPoAllocationService is:

        pct >= AvgSalesPctFrom  AND  pct <= AvgSalesPctTo      (first match by SortOrder)

    with NULL on either side meaning "unbounded". Every division has a full band
    set, but the lowest band starts above 0, so a store with no sales in that
    division (AvgSalesPct = 0.00) matches nothing and is persisted with an empty
    Grade. DivCode 421 also produced -6.00 — net negative sales, i.e. returns
    exceeding sales — which no positive floor can ever catch.

    On the 8/2026 BFLGROUP run that was 72 stores across all 20 divisions, every
    one of them at pct <= 0.

    Setting AvgSalesPctFrom = NULL on the bottom band is preferred over a large
    negative sentinel: the matcher already treats NULL as unbounded, and it
    cannot be out-run by a more negative value later.

    The bottom band is identified as MAX(SortOrder) per (Country, DivCode) among
    IsSpecial = 0 rows — SortOrder ascending runs highest grade first, so the
    largest SortOrder is the lowest grade.

    Idempotent: a band already NULL is left alone.
*/

SET NOCOUNT ON;

DECLARE @Country varchar(50) = 'BFLGROUP';   -- change and re-run per country

-- The bottom band of each division for this country.
;WITH bottom AS (
    SELECT r.*,
           ROW_NUMBER() OVER (PARTITION BY r.Country, r.DivCode
                              ORDER BY r.SortOrder DESC) AS rn
      FROM dbo.LPM_VolumeGroupRange_Country r
     WHERE r.Country = @Country
       AND r.IsSpecial = 0
)
SELECT 'BEFORE' AS Stage, DivCode, VolumeGroup, AvgSalesPctFrom, AvgSalesPctTo, SortOrder
  FROM bottom
 WHERE rn = 1
 ORDER BY DivCode;

;WITH bottom AS (
    SELECT r.*,
           ROW_NUMBER() OVER (PARTITION BY r.Country, r.DivCode
                              ORDER BY r.SortOrder DESC) AS rn
      FROM dbo.LPM_VolumeGroupRange_Country r
     WHERE r.Country = @Country
       AND r.IsSpecial = 0
)
UPDATE bottom
   SET AvgSalesPctFrom = NULL,
       UpdatedTS       = DATEADD(hour, 4, SYSUTCDATETIME()),
       UpdatedBy       = 'migration:open-bottom-band'
 WHERE rn = 1
   AND AvgSalesPctFrom IS NOT NULL;

PRINT CONCAT('Bottom bands opened for ', @Country, ': ', @@ROWCOUNT);

-- Verify.
;WITH bottom AS (
    SELECT r.*,
           ROW_NUMBER() OVER (PARTITION BY r.Country, r.DivCode
                              ORDER BY r.SortOrder DESC) AS rn
      FROM dbo.LPM_VolumeGroupRange_Country r
     WHERE r.Country = @Country
       AND r.IsSpecial = 0
)
SELECT 'AFTER' AS Stage, DivCode, VolumeGroup, AvgSalesPctFrom, AvgSalesPctTo, SortOrder
  FROM bottom
 WHERE rn = 1
 ORDER BY DivCode;

/*  After running: re-run Generate BFLGroup VG on the OTS for PO Allocation page.
    The success message should come back with no WARNING.

    The per-country band sets almost certainly have the same gap — their Monday
    06:00 runs will blank the same zero-sales stores. Re-run this with @Country
    set to each of BAHRAIN, ECOM, KSA, KUWAIT, MALAYSIA, OMAN, QATAR, UAE.
*/
