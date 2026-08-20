-- ============================================================
-- dbo.PatternCluster
-- Stores cluster quota patterns per customer, matching the shape
-- currently hardcoded in Logic/QuotaCatalog.cs (DraftQuotasByCustomer).
-- ============================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PatternCluster' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.PatternCluster
    (
        Id           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CustomerCode NVARCHAR(50)      NOT NULL,
        Section      NVARCHAR(50)      NOT NULL,
        Cluster      NVARCHAR(50)      NOT NULL,
        DeviceType   NVARCHAR(100)     NOT NULL,
        TargetCount  INT               NOT NULL CHECK (TargetCount >= 0),

        CONSTRAINT UQ_PatternCluster_Customer_Section_Cluster_DeviceType
            UNIQUE (CustomerCode, Section, Cluster, DeviceType)
    );
END
GO

-- ── Seed data: OILTEK / SECTION 2 pattern ──────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM dbo.PatternCluster WHERE CustomerCode = 'OILTEK')
BEGIN
    INSERT INTO dbo.PatternCluster (CustomerCode, Section, Cluster, DeviceType, TargetCount)
    VALUES
        -- CLUSTER 1
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Fan', 4),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'On/Off Valve', 6),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'High Level Switch', 6),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Low Level Switch', 4),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Control Valve', 4),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Level Transmitter', 2),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Pump', 5),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Pressure Switch', 3),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Pressure Transmitter', 2),
        ('OILTEK', 'SECTION 2', 'CLUSTER 1', 'Vibrator', 4),

        -- CLUSTER 2
        ('OILTEK', 'SECTION 2', 'CLUSTER 2', 'On/Off Valve', 5),
        ('OILTEK', 'SECTION 2', 'CLUSTER 2', 'Control Valve', 5),

        -- CLUSTER 3
        ('OILTEK', 'SECTION 2', 'CLUSTER 3', 'On/Off Valve', 4),

        -- CLUSTER 4
        ('OILTEK', 'SECTION 2', 'CLUSTER 4', 'On/Off Valve', 6),

        -- CLUSTER 5
        ('OILTEK', 'SECTION 2', 'CLUSTER 5', 'Pump', 4),
        ('OILTEK', 'SECTION 2', 'CLUSTER 5', 'Heater', 2),
        ('OILTEK', 'SECTION 2', 'CLUSTER 5', 'Temperature Transmitter', 3),
        ('OILTEK', 'SECTION 2', 'CLUSTER 5', 'Low Level Switch', 4),

        -- CLUSTER 6
        ('OILTEK', 'SECTION 2', 'CLUSTER 6', 'Control Valve', 7),
        ('OILTEK', 'SECTION 2', 'CLUSTER 6', 'On/Off Valve', 7),

        -- CLUSTER 7 — the model currently never predicts this cluster (per earlier report)
        ('OILTEK', 'SECTION 2', 'CLUSTER 7', 'On/Off Valve', 6),

        -- CLUSTER 8 — same issue, model never predicts this cluster
        ('OILTEK', 'SECTION 2', 'CLUSTER 8', 'Control Valve', 7);
END
GO
