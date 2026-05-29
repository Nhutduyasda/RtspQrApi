using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RtspQrApi.Data;

public sealed class RtspQrDbContextFactory : IDesignTimeDbContextFactory<RtspQrDbContext>
{
    public RtspQrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RtspQrDbContext>()
            .UseSqlServer(ProgramDefaults.ConnectionString)
            .Options;

        return new RtspQrDbContext(options);
    }
}
