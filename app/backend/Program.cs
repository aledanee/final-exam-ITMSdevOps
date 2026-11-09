using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------
var mySqlServerVersion = new MySqlServerVersion(new Version(8, 0, 36));

builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("DefaultConnection")
            ?? "Server=localhost;Port=3306;Database=taskdb;User=root;Password=root;AllowPublicKeyRetrieval=True;SslMode=None;",
        mySqlServerVersion,
        mysql => mysql.MigrationsAssembly(TaskDbContext.AssemblyName)));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// --- Database bootstrap ----------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var ct = CancellationToken.None;
    const int maxAttempts = 12;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            db.Database.EnsureCreated();

            // EnsureCreated() is a no-op when the database already exists
            // (e.g. created by db/init.sql or an old volume) — verify the table too.
            if (!await TableExistsAsync(db, ct))
            {
                await db.Database.ExecuteSqlRawAsync(TaskDbContext.CreateTableSql, ct);
            }

            break;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}. Retrying in 5s...",
                attempt, maxAttempts, ex.Message);
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
    }
}

static async Task<bool> TableExistsAsync(TaskDbContext db, CancellationToken ct) =>
    await db.Database
        .SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'Tasks') AS `Value`")
        .SingleAsync(ct);

// --- Middleware ------------------------------------------------------------
app.UseSwagger();
app.UseSwaggerUI();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (DbUpdateConcurrencyException)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = "The task was modified by another request." });
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

// --- Health check ----------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    utcTime = DateTime.UtcNow,
    service = "Task Manager API"
}))
.WithName("GetHealth")
.WithOpenApi();

// --- Task endpoints --------------------------------------------------------
app.MapGet("/api/tasks", async (TaskDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Tasks.AsNoTracking().OrderByDescending(t => t.Id).ToListAsync(ct)))
.WithName("GetTasks")
.WithOpenApi();

app.MapGet("/api/tasks/{id:int}", async (int id, TaskDbContext db, CancellationToken ct) =>
{
    var task = await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    return task is null ? Results.NotFound(new { error = $"Task {id} not found." }) : Results.Ok(task);
})
.WithName("GetTaskById")
.WithOpenApi();

app.MapPost("/api/tasks", async (CreateTaskRequest request, TaskDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "Title is required." });

    var task = new TaskItem
    {
        Title = request.Title.Trim(),
        Description = request.Description ?? string.Empty,
        IsCompleted = request.IsCompleted
    };

    db.Tasks.Add(task);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/tasks/{task.Id}", task);
})
.WithName("CreateTask")
.WithOpenApi();

app.MapPut("/api/tasks/{id:int}", async (int id, UpdateTaskRequest request, TaskDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "Title is required." });

    var task = await db.Tasks.FindAsync(new object[] { id }, ct);
    if (task is null)
        return Results.NotFound(new { error = $"Task {id} not found." });

    task.Title = request.Title.Trim();
    task.Description = request.Description ?? string.Empty;
    task.IsCompleted = request.IsCompleted;

    await db.SaveChangesAsync(ct);
    return Results.Ok(task);
})
.WithName("UpdateTask")
.WithOpenApi();

app.MapDelete("/api/tasks/{id:int}", async (int id, TaskDbContext db, CancellationToken ct) =>
{
    var task = await db.Tasks.FindAsync(new object[] { id }, ct);
    if (task is null)
        return Results.NotFound(new { error = $"Task {id} not found." });

    db.Tasks.Remove(task);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
})
.WithName("DeleteTask")
.WithOpenApi();

app.Run();

// --- Model, DbContext and DTOs (kept in this single file) ------------------

public record CreateTaskRequest(string Title, string? Description, bool IsCompleted = false);

public record UpdateTaskRequest(string Title, string? Description, bool IsCompleted);

public class TaskItem
{
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    public bool IsCompleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public const string AssemblyName = "TaskManager";

    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS Tasks (
            Id INT NOT NULL AUTO_INCREMENT,
            Title VARCHAR(200) NOT NULL,
            Description VARCHAR(2000) NOT NULL,
            IsCompleted TINYINT(1) NOT NULL DEFAULT 0,
            CreatedAt DATETIME(6) NOT NULL,
            PRIMARY KEY (Id),
            INDEX IX_Tasks_IsCompleted (IsCompleted)
        ) ENGINE = InnoDB;
        """;

    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Title).HasMaxLength(200).IsRequired();
            entity.Property(t => t.Description).HasMaxLength(2000);
            entity.Property(t => t.IsCompleted).HasDefaultValue(false);
            entity.HasIndex(t => t.IsCompleted);
        });
    }
}
