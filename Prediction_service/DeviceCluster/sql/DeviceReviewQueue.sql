USE XenCreator;
GO

-- ============================================================
-- dbo.DeviceReviewQueue
-- Replaces floating_deviceid.json + unallocated_device_ids.json.
-- One shared table, ProjectCode-filtered, Category column instead
-- of two separate files. Reclassifying a device (its Category
-- changes between runs) is a single UPDATE via MERGE — no more
-- cross-file reconciliation logic needed.
-- ============================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.tables
    WHERE name = 'DeviceReviewQueue'
      AND schema_id = SCHEMA_ID('dbo')
)
BEGIN
    CREATE TABLE dbo.DeviceReviewQueue
    (
        Id               INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Category         NVARCHAR(30)      NOT NULL CHECK (Category IN ('UnknownPrediction', 'Unallocated')),
        DumpedAt         DATETIME2         NOT NULL,
        Customer         NVARCHAR(50)      NOT NULL,
        ProjectCode      NVARCHAR(50)      NOT NULL,
        DeviceId         NVARCHAR(100)     NOT NULL,
        DeviceType       NVARCHAR(100)     NOT NULL,
        PredictedSection NVARCHAR(50)      NOT NULL,
        PredictedCluster NVARCHAR(50)      NOT NULL,
        Status           NVARCHAR(20)      NOT NULL DEFAULT 'pending' CHECK (Status IN ('pending', 'assigned')),

        CONSTRAINT UQ_DeviceReviewQueue_Device_Project
            UNIQUE (DeviceId, ProjectCode)
    );
END
GO
