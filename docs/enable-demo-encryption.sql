-- Optional local demo only; run AFTER --initialize.
-- Secret bytes are supplied through PROXY_KEY_LOCAL, never inserted into SQL.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
DECLARE @profile int;
SELECT @profile = Id FROM EncryptionProfiles WHERE Name = N'Local demo';
IF @profile IS NULL
BEGIN
    INSERT INTO EncryptionProfiles (Name, KeyReference, Enabled)
    VALUES (N'Local demo', N'LOCAL', 1);
    SET @profile = SCOPE_IDENTITY();
END;
UPDATE Operations SET EncryptionRequired = 1, EncryptionProfileId = @profile
WHERE [Key] = N'customer.get';
COMMIT;
-- To return to plaintext demo: UPDATE Operations SET EncryptionRequired = 0 WHERE [Key] = N'customer.get';
