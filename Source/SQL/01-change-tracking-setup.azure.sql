-- This script sets up the database for change tracking and creates necessary objects.
-- Create a contained user for the application with a strong password
--
-- Note: This is only for Azure SQL Database, which supports contained users.
-- If you are using SQL Server, you would typically create a login at the server level (of which we have another example file)

-- Create a contained user (Azure SQL only supports contained users)
IF USER_ID('DotNetWebApp') IS NULL
BEGIN
    CREATE USER [DotNetWebApp] WITH PASSWORD = 'a987REALLY#$%TRONGpa44w0rd!';
END
GO

-- Create schema if it doesn't exist
IF SCHEMA_ID('web') IS NULL
BEGIN
    EXEC('CREATE SCHEMA web');
END
GO

-- Grant execute permission on schema
GRANT EXECUTE ON SCHEMA::web TO [DotNetWebApp];
GO

-- Create sequence if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.sequences WHERE name = 'Ids')
BEGIN
    CREATE SEQUENCE dbo.Ids
        AS int
        START WITH 1;
END
GO

-- Drop and recreate table
DROP TABLE IF EXISTS dbo.TrainingSessions;
CREATE TABLE dbo.TrainingSessions
(
    [Id] int PRIMARY KEY NOT NULL DEFAULT (NEXT VALUE FOR dbo.Ids),
    [RecordedOn] datetimeoffset NOT NULL,
    [Type] varchar(50) NOT NULL,
    [Steps] int NOT NULL,
    [Distance] int NOT NULL, -- Meters
    [Duration] int NOT NULL, -- Seconds
    [Calories] int NOT NULL,
    [PostProcessedOn] datetimeoffset NULL,
    [AdjustedSteps] int NULL,
    [AdjustedDistance] decimal(9,6) NULL
);
GO

-- Enable change tracking on the database if not already enabled
IF NOT EXISTS (SELECT * FROM sys.change_tracking_databases WHERE database_id = DB_ID())
BEGIN
    DECLARE @dbname sysname = DB_NAME();
    EXEC('ALTER DATABASE [' + @dbname + '] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 30 DAYS, AUTO_CLEANUP = ON)');
END
GO

-- Enable change tracking on the TrainingSessions table if not already enabled
IF NOT EXISTS (SELECT * FROM sys.change_tracking_tables WHERE object_id = OBJECT_ID('dbo.TrainingSessions'))
BEGIN
    ALTER TABLE dbo.TrainingSessions ENABLE CHANGE_TRACKING;
END
GO
