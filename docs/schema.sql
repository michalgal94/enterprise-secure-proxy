IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE TABLE [ConfigurationChanges] (
        [Id] bigint NOT NULL IDENTITY,
        [OperationId] int NOT NULL,
        [ChangedAt] datetimeoffset NOT NULL,
        [Enabled] bit NOT NULL,
        [PreviousVersion] nvarchar(max) NOT NULL,
        CONSTRAINT [PK_ConfigurationChanges] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE TABLE [Destinations] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(max) NOT NULL,
        [BaseUrl] nvarchar(max) NOT NULL,
        [Enabled] bit NOT NULL,
        [TimeoutSeconds] int NOT NULL,
        [PermittedPort] int NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [Version] rowversion NOT NULL,
        CONSTRAINT [PK_Destinations] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE TABLE [EncryptionProfiles] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(max) NOT NULL,
        [KeyReference] nvarchar(max) NOT NULL,
        [Enabled] bit NOT NULL,
        CONSTRAINT [PK_EncryptionProfiles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE TABLE [ExecutionLogs] (
        [Id] uniqueidentifier NOT NULL,
        [CorrelationId] nvarchar(450) NOT NULL,
        [OperationKey] nvarchar(128) NULL,
        [DestinationId] int NULL,
        [StartedAt] datetimeoffset NOT NULL,
        [CompletedAt] datetimeoffset NULL,
        [DurationMs] bigint NOT NULL,
        [Forwarded] bit NOT NULL,
        [UpstreamStatus] int NULL,
        [ProxyStatus] int NOT NULL,
        [Outcome] nvarchar(max) NOT NULL,
        [Reason] nvarchar(80) NULL,
        CONSTRAINT [PK_ExecutionLogs] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE TABLE [Operations] (
        [Id] int NOT NULL IDENTITY,
        [Key] nvarchar(128) NOT NULL,
        [DestinationId] int NOT NULL,
        [Method] nvarchar(max) NOT NULL,
        [Path] nvarchar(max) NOT NULL,
        [Enabled] bit NOT NULL,
        [MaxRequestBytes] int NOT NULL,
        [MaxResponseBytes] int NOT NULL,
        [AllowUnknownParameters] bit NOT NULL,
        [EncryptionRequired] bit NOT NULL,
        [EncryptionProfileId] int NULL,
        [Version] rowversion NOT NULL,
        CONSTRAINT [PK_Operations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Operations_Destinations_DestinationId] FOREIGN KEY ([DestinationId]) REFERENCES [Destinations] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_Operations_EncryptionProfiles_EncryptionProfileId] FOREIGN KEY ([EncryptionProfileId]) REFERENCES [EncryptionProfiles] ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE TABLE [HeaderRules] (
        [Id] int NOT NULL IDENTITY,
        [OperationId] int NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_HeaderRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_HeaderRules_Operations_OperationId] FOREIGN KEY ([OperationId]) REFERENCES [Operations] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE TABLE [ParameterRules] (
        [Id] int NOT NULL IDENTITY,
        [OperationId] int NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Required] bit NOT NULL,
        [Type] nvarchar(max) NOT NULL,
        [Location] nvarchar(max) NOT NULL,
        [MaxLength] int NOT NULL,
        [Minimum] decimal(18,4) NULL,
        [Maximum] decimal(18,4) NULL,
        CONSTRAINT [PK_ParameterRules] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_ParameterRules_Operations_OperationId] FOREIGN KEY ([OperationId]) REFERENCES [Operations] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_ExecutionLogs_CorrelationId] ON [ExecutionLogs] ([CorrelationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_HeaderRules_OperationId_Name] ON [HeaderRules] ([OperationId], [Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Operations_DestinationId] ON [Operations] ([DestinationId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE INDEX [IX_Operations_EncryptionProfileId] ON [Operations] ([EncryptionProfileId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Operations_Key] ON [Operations] ([Key]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ParameterRules_OperationId_Name] ON [ParameterRules] ([OperationId], [Name]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261112082401_InitialSchema'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261112082401_InitialSchema', N'8.0.34');
END;
GO

COMMIT;
GO

