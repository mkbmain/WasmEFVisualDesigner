using EfSchemaVisualizer.Core.CodeGen;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class OnModelCreatingRewriterTests
{
    private const string Source = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email).HasMaxLength(255);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_ChangesOnlyTargetedCall_LeavesEverythingElseIdentical()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(Source, entityName: "Person", propertyName: "Name", newMaxLength: 150);

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(150)", result);

        // Untouched: Person.Email, Address.Line1, and the unrelated comment.
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        Assert.DoesNotContain("HasMaxLength(100)", result);
    }

    [Fact]
    public void RewriteMaxLength_UnknownEntity_Throws()
    {
        var rewriter = new OnModelCreatingRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RewriteMaxLength(Source, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10));
    }
}
