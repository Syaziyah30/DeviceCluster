USE XenCreator;
GO

-- ============================================================
-- dbo.OutputDeviceAssignment
-- Persists the successfully assigned devices (cluster groups) that
-- DevicePipeline.RunAsync currently only returns in-memory to the
-- caller. Same pattern as dbo.DeviceReviewQueue: one row per device,
-- upserted via MERGE keyed on (DeviceId, ProjectCode), so a device
-- that gets reassigned on a later run just updates in place.
-- ============================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.tables
    WHERE name = 'OutputDeviceAssignment'
      AND schema_id = SCHEMA_ID('dbo')
)
BEGIN
    CREATE TABLE dbo.OutputDeviceAssignment
    (
        Id              INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        AssignedAt      DATETIME2         NOT NULL,
        Customer        NVARCHAR(50)      NOT NULL,
        ProjectCode     NVARCHAR(50)      NOT NULL,
        DeviceId        NVARCHAR(100)     NOT NULL,
        DeviceType      NVARCHAR(100)     NOT NULL,
        Section         NVARCHAR(50)      NOT NULL,
        Cluster         NVARCHAR(50)      NOT NULL,
        Confidence      FLOAT             NOT NULL,
        IsBackfill      BIT               NOT NULL DEFAULT 0,
        OriginalCluster NVARCHAR(50)      NULL,

        CONSTRAINT UQ_OutputDeviceAssignment_Device_Project
            UNIQUE (DeviceId, ProjectCode)
    );
END
GO
