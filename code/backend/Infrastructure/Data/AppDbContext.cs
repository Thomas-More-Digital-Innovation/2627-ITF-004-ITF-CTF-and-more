using Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace CA.Infrastructure.Data
{
    public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        // public DbSet<Post> Posts => Set<Post>();
    }
}
