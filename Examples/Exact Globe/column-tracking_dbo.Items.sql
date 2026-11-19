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

-- Enable column-level change tracking on the table if not already enabled
IF NOT EXISTS (SELECT * FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID('dbo.Items'))
BEGIN
    ALTER TABLE dbo.Items ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON);
END
GO

CREATE OR ALTER PROCEDURE web.get_itemssync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion BIGINT = JSON_VALUE(@json, '$.fromVersion');

    -- Column IDs for the change tracking column mask
    DECLARE @Description INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'Description', 'ColumnId');
    DECLARE @Assortment INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'Assortment', 'ColumnId');
    DECLARE @Sysguid INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'sysguid', 'ColumnId');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;

        DECLARE @curVer BIGINT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer BIGINT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Items'));

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
                    SELECT ItemCode, Description, Assortment, sysguid
                    FROM dbo.Items
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
                        -- FOR JSON PATH omits NULL properties: $changed separates a column set to NULL from an unchanged column
                        CASE WHEN ct.SYS_CHANGE_OPERATION = 'U' THEN JSON_QUERY((
                            SELECT '["' + STRING_AGG(c.name, '","') + '"]'
                            FROM (VALUES
                                ('Description', @Description),
                                ('Assortment',  @Assortment),
                                ('sysguid',     @Sysguid)
                            ) AS c(name, colid)
                            WHERE CHANGE_TRACKING_IS_COLUMN_IN_MASK(c.colid, ct.SYS_CHANGE_COLUMNS) = 1
                        )) END AS '$changed',
                        ct.ItemCode,
                        CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Description, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.Description END AS Description,
                        CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Assortment, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.Assortment END AS Assortment,
                        CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Sysguid, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.sysguid END AS sysguid
                    FROM CHANGETABLE(CHANGES dbo.Items, @fromVersion) AS ct
                    LEFT OUTER JOIN dbo.Items AS i ON i.ItemCode = ct.ItemCode
                    WHERE ct.SYS_CHANGE_OPERATION != 'U'
                       OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Description, ct.SYS_CHANGE_COLUMNS) = 1
                       OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Assortment, ct.SYS_CHANGE_COLUMNS) = 1
                       OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Sysguid, ct.SYS_CHANGE_COLUMNS) = 1
                    FOR JSON PATH
                ))
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
        END

    COMMIT TRAN;
END
GO
