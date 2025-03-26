using Microsoft.EntityFrameworkCore;
using WrapperApi.Models;

namespace WrapperApi.Data;

public class DataContext : DbContext
{
    public DataContext(DbContextOptions<DataContext> options) : base(options) { }

    public DbSet<Job> Jobs => Set<Job>();
}