using Microsoft.EntityFrameworkCore;
namespace SecureProxy;
public static class ConfigurationAdmin
{
    public static async Task<IResult> SetEnabledAsync(ProxyDb db, string key, EnableRequest request, CancellationToken token, ConfigurationCache? cache = null, string actorId = "local-admin", string correlationId = "")
    {
        var op = await db.Operations.SingleOrDefaultAsync(x => x.Key == key, token);
        if (op is null) return PublicErrors.Result(404, "unknown_operation", correlationId);
        if (string.IsNullOrEmpty(request.Version)) return PublicErrors.Result(400, "invalid_version", correlationId);
        byte[] version;
        try { version = Convert.FromBase64String(request.Version); }
        catch (FormatException) { return PublicErrors.Result(400, "invalid_version", correlationId); }
        if (version.Length != 8 || !version.SequenceEqual(op.Version)) return PublicErrors.Result(409, "configuration_conflict", correlationId);
        db.Entry(op).Property(x => x.Version).OriginalValue = version;
        op.Enabled = request.Enabled;
        // Force UPDATE even for an unchanged value so SQL verifies rowversion.
        db.Entry(op).Property(x => x.Enabled).IsModified = true;
        db.ConfigurationChanges.Add(new() { OperationId = op.Id, Enabled = request.Enabled, PreviousVersion = request.Version, ActorId = actorId });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return PublicErrors.Result(409, "configuration_conflict", correlationId); }
        cache?.Invalidate();
        return Results.Ok(new { op.Enabled, version = Convert.ToBase64String(op.Version) });
    }
}
