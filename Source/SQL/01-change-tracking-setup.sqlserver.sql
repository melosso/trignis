-- SQL Server setup: change tracking, a login and a user. For Azure SQL Database, use 01-change-tracking-setup.azure.sql.

-- Create a login at the server level if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.server_principals WHERE name = N'DotNetWebApp')
BEGIN
    CREATE LOGIN [DotNetWebApp] WITH PASSWORD = 'a987REALLY#$%TRONGpa44w0rd!';
END
GO

-- Create a user in the current database mapped to that login
IF USER_ID('DotNetWebApp') IS NULL
BEGIN
    CREATE USER [DotNetWebApp] FOR LOGIN [DotNetWebApp];
END
GO

-- Create schema if missing
IF SCHEMA_ID('web') IS NULL
BEGIN
    EXEC('CREATE SCHEMA web');
END
GO

-- Grant execute permission on the web schema to the user
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

-- Drop and recreate the TrainingSessions table
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
