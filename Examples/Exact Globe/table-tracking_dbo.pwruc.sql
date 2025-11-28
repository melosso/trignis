-- Create schema if missing
IF SCHEMA_ID('web') IS NULL
BEGIN
    EXEC('CREATE SCHEMA web');
END
GO

-- Enable change tracking on the database if not already enabled
IF NOT EXISTS (SELECT * FROM sys.change_tracking_databases WHERE database_id = DB_ID())
BEGIN
    DECLARE @dbname sysname = DB_NAME();
    EXEC('ALTER DATABASE [' + @dbname + '] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 30 DAYS, AUTO_CLEANUP = ON)');
END
GO

-- Enable snapshot isolation
ALTER DATABASE CURRENT  
SET ALLOW_SNAPSHOT_ISOLATION ON  
GO 

-- Drop and recreate the pwruc table
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE Name = 'pwruc')
BEGIN 
    CREATE TABLE [dbo].[pwruc](
        [ID] [int] IDENTITY(1,1) NOT NULL,
        [res_id] [int] NOT NULL,
        [role_id] [int] NOT NULL,
        [rolelevel] [int] NOT NULL,
        [Division] [smallint] NULL,
        [syscreated] [datetime] NOT NULL,
        [syscreator] [int] NOT NULL,
        [sysmodified] [datetime] NOT NULL,
        [sysmodifier] [int] NOT NULL,
        [sysguid] [uniqueidentifier] NOT NULL,
        [timestamp] [timestamp] NOT NULL,
    CONSTRAINT [PK_pwruc] PRIMARY KEY NONCLUSTERED 
    (
        [ID] ASC
    )WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
    ) ON [PRIMARY]
END
GO

-- Enable change tracking on the table if not already enabled
IF NOT EXISTS (SELECT * FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID('dbo.pwruc'))
BEGIN
    ALTER TABLE dbo.pwruc ENABLE CHANGE_TRACKING;
END
GO

CREATE OR ALTER PROCEDURE web.get_pwrucsync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion INT = JSON_VALUE(@json, '$.fromVersion');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;

        DECLARE @curVer INT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer INT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.pwruc'));

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
                    SELECT ID, res_id, role_id, rolelevel, Division, syscreated, syscreator, sysmodified, sysmodifier, sysguid, timestamp
                    FROM dbo.pwruc
                    FOR JSON AUTO
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
                        ct.ID,
                        p.res_id,
                        p.role_id,
                        p.rolelevel,
                        p.Division,
                        p.syscreated,
                        p.syscreator,
                        p.sysmodified,
                        p.sysmodifier,
                        p.sysguid,
                        b.bedrnr As CompanyNumber
                    FROM dbo.pwruc AS p
					CROSS JOIN dbo.bedryf b
                    RIGHT OUTER JOIN
                        CHANGETABLE(CHANGES dbo.pwruc, @fromVersion) AS ct ON ct.ID = p.ID
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END

    COMMIT TRAN;
END
GO