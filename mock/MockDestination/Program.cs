var app = WebApplication.CreateBuilder(args).Build();
var received = 0;
app.MapGet("/customers/{id:int}", (int id) =>
{
    Interlocked.Increment(ref received);
    return Results.Ok(new { id, name = "Assessment Customer" });
});
app.MapGet("/__test/request-count", () => Results.Ok(new { count = Volatile.Read(ref received) }));
app.Run();
