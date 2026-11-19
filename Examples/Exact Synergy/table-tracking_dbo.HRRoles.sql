-- Create schema if missing
IF SCHEMA_ID('web') IS NULL
BEGIN
    EXEC('CREATE SCHEMA web');
END
GO

-- Enable change tracking on the database if not already enabled
IF NOT EXISTS (SELECT * FROM sys.change_tracking_databases WHERE database_id = DB_ID())
BEGIN
    ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 30 DAYS, AUTO_CLEANUP = ON);
END
GO

-- Enable snapshot isolation; the procedure reads the version and the rows in one snapshot
ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

-- Create the table if missing
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE Name = 'HRRoles')
BEGIN
    CREATE TABLE dbo.HRRoles
    (
        ID int PRIMARY KEY,
        RoleID int,
        EmpID int,
        RoleLevel int,
        Division int
    );
END
GO

-- Enable change tracking on the table if not already enabled
IF NOT EXISTS (SELECT * FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID('dbo.HRRoles'))
BEGIN
    ALTER TABLE dbo.HRRoles ENABLE CHANGE_TRACKING;
END
GO

CREATE OR ALTER PROCEDURE web.get_hrrolessync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion BIGINT = JSON_VALUE(@json, '$.fromVersion');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;

        DECLARE @curVer BIGINT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer BIGINT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.HRRoles'));

        IF (@fromVersion = 0)
        BEGIN
            SET @reason = 0; -- First Sync
        END
        ELSE IF (@fromVersion < @minVer)
        BEGIN
            SET @fromVersion = 0;
            SET @reason = 1; -- fromVersion too old. New full sync needed
        END

        IF (@fromVersion = 0)
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Full' AS 'Metadata.Sync.Type',
                @reason AS 'Metadata.Sync.ReasonCode',
                [Data] = JSON_QUERY((
                    SELECT hr.ID, hr.RoleID, hr.EmpID, hr.RoleLevel, hr.Division, b.bedrnr AS CompanyNumber
                    FROM dbo.HRRoles AS hr
                    CROSS JOIN dbo.bedryf AS b
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END
        ELSE
        BEGIN
            SELECT
                @curVer AS 'Metadata.Sync.Version',
                'Diff' AS 'Metadata.Sync.Type',
                [Data] = JSON_QUERY((
                    SELECT
                        ct.SYS_CHANGE_OPERATION AS '$operation',
                        ct.SYS_CHANGE_VERSION AS '$version',
                        ct.ID, hr.RoleID, hr.EmpID, hr.RoleLevel, hr.Division,
                        b.bedrnr AS CompanyNumber
                    FROM CHANGETABLE(CHANGES dbo.HRRoles, @fromVersion) AS ct
                    LEFT OUTER JOIN dbo.HRRoles AS hr ON hr.ID = ct.ID
                    CROSS JOIN dbo.bedryf AS b
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END

    COMMIT TRAN;
END
GO
