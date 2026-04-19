IF OBJECT_ID('dbo.Pages', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Pages (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        OriginalUrl NVARCHAR(2048) NOT NULL UNIQUE,
        LocalPath NVARCHAR(2048) NOT NULL,
        Status NVARCHAR(32) NOT NULL,
        CreatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_Pages_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_Pages_UpdatedAt DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF OBJECT_ID('dbo.Assets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Assets (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        OriginalUrl NVARCHAR(2048) NOT NULL UNIQUE,
        LocalPath NVARCHAR(2048) NOT NULL,
        PageId BIGINT NOT NULL,
        CreatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_Assets_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Assets_Pages_PageId FOREIGN KEY (PageId) REFERENCES dbo.Pages(Id)
    );
END;
GO

IF OBJECT_ID('dbo.CrawlQueue', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CrawlQueue (
        Id BIGINT IDENTITY(1,1) PRIMARY KEY,
        Url NVARCHAR(2048) NOT NULL UNIQUE,
        Status NVARCHAR(32) NOT NULL,
        Depth INT NOT NULL,
        MaxDepth INT NOT NULL,
        RetryCount INT NOT NULL CONSTRAINT DF_CrawlQueue_RetryCount DEFAULT 0,
        ErrorMessage NVARCHAR(4000) NULL,
        CreatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_CrawlQueue_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt DATETIMEOFFSET NOT NULL CONSTRAINT DF_CrawlQueue_UpdatedAt DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrawlQueue_Status_CreatedAt' AND object_id = OBJECT_ID('dbo.CrawlQueue'))
BEGIN
    CREATE INDEX IX_CrawlQueue_Status_CreatedAt ON dbo.CrawlQueue(Status, CreatedAt);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Pages_Status' AND object_id = OBJECT_ID('dbo.Pages'))
BEGIN
    CREATE INDEX IX_Pages_Status ON dbo.Pages(Status);
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Assets_PageId' AND object_id = OBJECT_ID('dbo.Assets'))
BEGIN
    CREATE INDEX IX_Assets_PageId ON dbo.Assets(PageId);
END;
GO
