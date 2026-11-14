-- Column-level tracking for SQL Server: emits the columns that an update changed. Uses dbo.Items from the documentation and creates the table: the script has no dependency on 01.

IF OBJECT_ID('dbo.Items') IS NULL
BEGIN
    CREATE TABLE dbo.Items (
        ItemCode    VARCHAR(20)      NOT NULL PRIMARY KEY,
        Description VARCHAR(200)     NULL,
        Assortment  VARCHAR(50)      NULL,
        sysguid     UNIQUEIDENTIFIER NULL
    );
END
GO

-- Column tracking must be enabled explicitly: SYS_CHANGE_COLUMNS is empty without it.
IF NOT EXISTS (SELECT * FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID('dbo.Items'))
BEGIN
    ALTER TABLE dbo.Items ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = ON);
END
GO

CREATE OR ALTER PROCEDURE web.get_itemssync
    @json NVARCHAR(MAX)
AS
BEGIN
    DECLARE @fromVersion INT = JSON_VALUE(@json, '$.fromVersion');

    -- Column IDs for change tracking (used to check which columns changed in updates)
    DECLARE @Description INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'Description', 'ColumnId');
    DECLARE @Assortment INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'Assortment', 'ColumnId');
    DECLARE @Sysguid INT = COLUMNPROPERTY(OBJECT_ID('dbo.Items'), 'sysguid', 'ColumnId');

    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRAN;
        DECLARE @reason INT;

        DECLARE @curVer INT = CHANGE_TRACKING_CURRENT_VERSION();
        DECLARE @minVer INT = CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.Items'));

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
						-- Names of changed columns. FOR JSON PATH omits null properties: without $changed a consumer cannot distinguish a column set to NULL from an unchanged column.
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
						CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Description, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.Description ELSE NULL END AS Description,
						CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Assortment, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.Assortment ELSE NULL END AS Assortment,
						CASE WHEN ct.SYS_CHANGE_OPERATION != 'U' OR CHANGE_TRACKING_IS_COLUMN_IN_MASK(@Sysguid, ct.SYS_CHANGE_COLUMNS) = 1 THEN i.sysguid ELSE NULL END AS sysguid
					FROM dbo.Items AS i
					RIGHT OUTER JOIN CHANGETABLE(CHANGES dbo.Items, @fromVersion) AS ct
						ON ct.ItemCode = i.ItemCode
					WHERE ct.SYS_CHANGE_OPERATION != 'U'  -- Include all inserts and deletes
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

-- Payload semantics for consumers: https://melosso.github.io/trignis/reference/stored-procedure (SQL Server, Column-level tracking). Do not use INCLUDE_NULL_VALUES.
