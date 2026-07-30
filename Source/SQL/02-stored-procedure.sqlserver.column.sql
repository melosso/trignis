-- Column-level tracking for SQL Server: emit only the columns an update actually touched.
--
-- This example uses dbo.Items, matching the walkthrough in the documentation, rather than the
-- dbo.TrainingSessions table created by 01. It therefore creates what it needs, so the script
-- runs whichever order you work through the folder in.
--
-- Without this, SQL Server would still create the procedure without complaint (name resolution
-- is deferred) and only fail with "Invalid object name" when Trignis first calls it.

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

-- Column tracking has to be switched on explicitly, or SYS_CHANGE_COLUMNS is always empty
-- and every update reports that nothing changed.
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
						-- Which columns actually moved. FOR JSON PATH omits null properties, so
						-- without this a column cleared to NULL and a column left untouched both
						-- arrive as an absent key and a consumer cannot tell them apart. It would
						-- silently drop the clearing, and the version advances past it for good.
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

-- Reading the payload downstream:
--
--   $operation = 'I'  the full row, though NULL columns are omitted by FOR JSON PATH, so a
--                     missing key on an insert simply means that column is NULL
--   $operation = 'U'  $changed lists the columns that moved. For each name in that list, a
--                     matching key carries the new value, and a missing key means the column
--                     was set to NULL. Columns in neither place did not change.
--   $operation = 'D'  only the key columns are present, the row is gone
--
-- Do not reach for INCLUDE_NULL_VALUES to solve this. It emits every unchanged column as null
-- as well, which reintroduces exactly the ambiguity $changed exists to remove.
