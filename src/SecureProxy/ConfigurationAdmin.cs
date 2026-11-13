using Microsoft.EntityFrameworkCore;
namespace SecureProxy;
public static class ConfigurationAdmin
{
    public static async Task<IResult> SetEnabledAsync(ProxyDb db, string key, EnableRequest request, CancellationToken token)
    {
        var op = await db.Operations.SingleOrDefaultAsync(x => x.Key == key, token);
        if (op is null) return Results.NotFound();
        if (string.IsNullOrEmpty(request.Version)) return Results.BadRequest(new { code = "invalid_version" });
        byte[] version;
        try { version = Convert.FromBase64String(request.Version); }
        catch (FormatException) { return Results.BadRequest(new { code = "invalid_version" }); }
        if (version.Length != 8 || !version.SequenceEqual(op.Version)) return Results.Conflict(new { code = "configuration_conflict" });
        db.Entry(op).Property(x => x.Version).OriginalValue = version;
        op.Enabled = request.Enabled;
        // Force UPDATE even for an unchanged value so SQL verifies rowversion.
        db.Entry(op).Property(x => x.Enabled).IsModified = true;
        db.ConfigurationChanges.Add(new() { OperationId = op.Id, Enabled = request.Enabled, PreviousVersion = request.Version });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { return Results.Conflict(new { code = "configuration_conflict" }); }
        return Results.Ok(new { op.Enabled, version = Convert.ToBase64String(op.Version) });
    }
}
