USE XenCreator;
GO

-- ============================================================
-- dbo.PatternCluster
-- Stores cluster quota patterns per customer
-- ============================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.tables
    WHERE name = 'PatternCluster'
      AND schema_id = SCHEMA_ID('dbo')
)
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

-- Seed data: OILTEK / SECTION 2 pattern — REAL, confirmed pattern
IF NOT EXISTS (
    SELECT 1
    FROM dbo.PatternCluster
    WHERE CustomerCode = 'OILTEK' AND Section = 'SECTION 2'
)
BEGIN
    INSERT INTO dbo.PatternCluster
        (CustomerCode, Section, Cluster, DeviceType, TargetCount)
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

        -- CLUSTER 7
        ('OILTEK', 'SECTION 2', 'CLUSTER 7', 'On/Off Valve', 6),

        -- CLUSTER 8
        ('OILTEK', 'SECTION 2', 'CLUSTER 8', 'Control Valve', 7);
END
GO

-- ============================================================
-- ⚠️ DUMMY DATA BELOW — SECTION 1, 3-8
-- Randomly fabricated placeholder patterns, NOT real business rules.
-- Only SECTION 2 above reflects a confirmed real quota pattern.
-- Replace these rows with real numbers as each section's real pattern is confirmed.
-- ============================================================
IF NOT EXISTS (
    SELECT 1
    FROM dbo.PatternCluster
    WHERE CustomerCode = 'OILTEK' AND Section = 'SECTION 1'
)
BEGIN
    INSERT INTO dbo.PatternCluster
        (CustomerCode, Section, Cluster, DeviceType, TargetCount)
    VALUES
        -- SECTION 1
        ('OILTEK', 'SECTION 1', 'CLUSTER 1', 'On/Off Valve', 5),
        ('OILTEK', 'SECTION 1', 'CLUSTER 1', 'Pump', 3),
        ('OILTEK', 'SECTION 1', 'CLUSTER 1', 'Pressure Transmitter', 2),
        ('OILTEK', 'SECTION 1', 'CLUSTER 2', 'Control Valve', 4),
        ('OILTEK', 'SECTION 1', 'CLUSTER 2', 'Fan', 2),
        ('OILTEK', 'SECTION 1', 'CLUSTER 3', 'High Level Switch', 3),
        ('OILTEK', 'SECTION 1', 'CLUSTER 3', 'Low Level Switch', 3),
        ('OILTEK', 'SECTION 1', 'CLUSTER 3', 'Vibrator', 2),
        ('OILTEK', 'SECTION 1', 'CLUSTER 4', 'Heater', 3),
        ('OILTEK', 'SECTION 1', 'CLUSTER 4', 'Temperature Transmitter', 2),

        -- SECTION 3
        ('OILTEK', 'SECTION 3', 'CLUSTER 1', 'On/Off Valve', 6),
        ('OILTEK', 'SECTION 3', 'CLUSTER 1', 'Control Valve', 3),
        ('OILTEK', 'SECTION 3', 'CLUSTER 2', 'Pump', 4),
        ('OILTEK', 'SECTION 3', 'CLUSTER 2', 'Pressure Switch', 2),
        ('OILTEK', 'SECTION 3', 'CLUSTER 3', 'Level Transmitter', 3),
        ('OILTEK', 'SECTION 3', 'CLUSTER 3', 'Fan', 2),

        -- SECTION 4
        ('OILTEK', 'SECTION 4', 'CLUSTER 1', 'On/Off Valve', 4),
        ('OILTEK', 'SECTION 4', 'CLUSTER 1', 'High Level Switch', 2),
        ('OILTEK', 'SECTION 4', 'CLUSTER 2', 'Control Valve', 5),
        ('OILTEK', 'SECTION 4', 'CLUSTER 2', 'Low Level Switch', 3),
        ('OILTEK', 'SECTION 4', 'CLUSTER 3', 'Pump', 3),
        ('OILTEK', 'SECTION 4', 'CLUSTER 3', 'Heater', 2),
        ('OILTEK', 'SECTION 4', 'CLUSTER 4', 'Vibrator', 3),
        ('OILTEK', 'SECTION 4', 'CLUSTER 4', 'Pressure Transmitter', 2),
        ('OILTEK', 'SECTION 4', 'CLUSTER 5', 'Temperature Transmitter', 4),
        ('OILTEK', 'SECTION 4', 'CLUSTER 5', 'Fan', 1),

        -- SECTION 5
        ('OILTEK', 'SECTION 5', 'CLUSTER 1', 'On/Off Valve', 7),
        ('OILTEK', 'SECTION 5', 'CLUSTER 1', 'Control Valve', 4),
        ('OILTEK', 'SECTION 5', 'CLUSTER 2', 'Pump', 2),
        ('OILTEK', 'SECTION 5', 'CLUSTER 2', 'Pressure Switch', 3),
        ('OILTEK', 'SECTION 5', 'CLUSTER 3', 'Level Transmitter', 2),
        ('OILTEK', 'SECTION 5', 'CLUSTER 3', 'High Level Switch', 2),

        -- SECTION 6
        ('OILTEK', 'SECTION 6', 'CLUSTER 1', 'Control Valve', 6),
        ('OILTEK', 'SECTION 6', 'CLUSTER 1', 'On/Off Valve', 5),
        ('OILTEK', 'SECTION 6', 'CLUSTER 2', 'Fan', 3),
        ('OILTEK', 'SECTION 6', 'CLUSTER 2', 'Vibrator', 2),
        ('OILTEK', 'SECTION 6', 'CLUSTER 3', 'Heater', 3),
        ('OILTEK', 'SECTION 6', 'CLUSTER 3', 'Temperature Transmitter', 3),
        ('OILTEK', 'SECTION 6', 'CLUSTER 4', 'Low Level Switch', 4),
        ('OILTEK', 'SECTION 6', 'CLUSTER 4', 'Pressure Transmitter', 2),

        -- SECTION 7
        ('OILTEK', 'SECTION 7', 'CLUSTER 1', 'On/Off Valve', 5),
        ('OILTEK', 'SECTION 7', 'CLUSTER 1', 'Pump', 3),
        ('OILTEK', 'SECTION 7', 'CLUSTER 2', 'Control Valve', 4),
        ('OILTEK', 'SECTION 7', 'CLUSTER 2', 'Pressure Switch', 2),

        -- SECTION 8
        ('OILTEK', 'SECTION 8', 'CLUSTER 1', 'Fan', 3),
        ('OILTEK', 'SECTION 8', 'CLUSTER 1', 'On/Off Valve', 4),
        ('OILTEK', 'SECTION 8', 'CLUSTER 2', 'Control Valve', 5),
        ('OILTEK', 'SECTION 8', 'CLUSTER 2', 'High Level Switch', 3),
        ('OILTEK', 'SECTION 8', 'CLUSTER 3', 'Pump', 4),
        ('OILTEK', 'SECTION 8', 'CLUSTER 3', 'Low Level Switch', 2),
        ('OILTEK', 'SECTION 8', 'CLUSTER 4', 'Heater', 2),
        ('OILTEK', 'SECTION 8', 'CLUSTER 4', 'Vibrator', 3),
        ('OILTEK', 'SECTION 8', 'CLUSTER 5', 'Temperature Transmitter', 3),
        ('OILTEK', 'SECTION 8', 'CLUSTER 5', 'Level Transmitter', 2),
        ('OILTEK', 'SECTION 8', 'CLUSTER 6', 'Pressure Transmitter', 2),
        ('OILTEK', 'SECTION 8', 'CLUSTER 6', 'Pressure Switch', 3);
END
GO
