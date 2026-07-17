/*
 * LPMSIM.dbo.WmsCountryOtsWeeks
 *
 * Number-of-weeks-of-Week-Sales-to-include per SIM country, used by the
 * "OTS for PO Allocation" report. No UI to edit — DBA/Ops updates by SQL.
 *
 * Run inside LPMSIM on the on-prem backup server. Idempotent.
 */

IF OBJECT_ID(N'dbo.WmsCountryOtsWeeks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WmsCountryOtsWeeks
    (
        SimCountry NVARCHAR(20) NOT NULL,
        Weeks      INT          NOT NULL,
        CONSTRAINT PK_WmsCountryOtsWeeks PRIMARY KEY (SimCountry)
    );
END;

;WITH src AS (
    SELECT SimCountry, Weeks FROM (VALUES
        ('KSA',      4),
        ('UAE',      1),
        ('MALAYSIA', 8),
        ('OMAN',     2),
        ('BAHRAIN',  3),
        ('QAT',      3),
        ('KWT',      3)
    ) AS v(SimCountry, Weeks)
)
MERGE dbo.WmsCountryOtsWeeks AS tg
USING src ON tg.SimCountry = src.SimCountry
WHEN MATCHED AND tg.Weeks <> src.Weeks THEN UPDATE SET Weeks = src.Weeks
WHEN NOT MATCHED BY TARGET THEN INSERT (SimCountry, Weeks) VALUES (src.SimCountry, src.Weeks);
