using Microsoft.EntityFrameworkCore;

namespace SocketWeb.Data;

// SocketWeb is the only project that owns database access - neither
// SocketDesktop nor the browser ever talk to MySQL directly. This
// DbContext is registered once in Program.cs and injected wherever it's
// needed (currently just ChatRepository).
public class ChatDbContext : DbContext
{
    public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
    {
    }

    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Title).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Role).HasMaxLength(16).IsRequired();

            // Chat messages can be long, so store them as MySQL's
            // unbounded LONGTEXT rather than a length-limited VARCHAR.
            entity.Property(message => message.Content).HasColumnType("longtext").IsRequired();

            // Messages are always read "give me session X in order", so
            // this index covers exactly that query.
            entity.HasIndex(message => new { message.SessionId, message.Sequence });

            // Deleting a session deletes its messages with it - enforced
            // at the database level (ON DELETE CASCADE), not just in C#.
            entity.HasOne(message => message.Session)
                  .WithMany(session => session.Messages)
                  .HasForeignKey(message => message.SessionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
