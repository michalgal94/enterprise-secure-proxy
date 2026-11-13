-- Run against the proxy database as an authorized configuration operator.
-- Set the exact JWT subject and one existing operation key before executing.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @Subject nvarchar(128) = N'replace-with-token-subject';
DECLARE @OperationKey nvarchar(128) = N'customer.get';
DECLARE @OperationId int = (SELECT Id FROM Operations WHERE [Key] = @OperationKey);

IF @Subject = N'replace-with-token-subject' OR LEN(@Subject) = 0
    THROW 50001, 'Set the caller subject before running this script.', 1;
IF @OperationId IS NULL
    THROW 50002, 'The configured operation does not exist.', 1;

IF NOT EXISTS (SELECT 1 FROM Callers WHERE Id = @Subject)
    INSERT INTO Callers (Id, Enabled) VALUES (@Subject, 1);

IF NOT EXISTS (SELECT 1 FROM CallerOperationGrants WHERE CallerId = @Subject AND OperationId = @OperationId)
    INSERT INTO CallerOperationGrants (CallerId, OperationId) VALUES (@Subject, @OperationId);

COMMIT;

-- Existing disabled callers stay disabled. Re-enable them only after review.
-- Revoke this operation with DELETE FROM CallerOperationGrants
-- WHERE CallerId = @Subject AND OperationId = @OperationId;
