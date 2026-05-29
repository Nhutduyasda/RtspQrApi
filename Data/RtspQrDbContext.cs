using Microsoft.EntityFrameworkCore;

namespace RtspQrApi.Data;

public sealed class RtspQrDbContext : DbContext
{
    public RtspQrDbContext(DbContextOptions<RtspQrDbContext> options)
        : base(options)
    {
    }

    public DbSet<QrScanRecord> QrScanRecords => Set<QrScanRecord>();

    public DbSet<CameraConfigRecord> CameraConfigs => Set<CameraConfigRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var cameraConfig = modelBuilder.Entity<CameraConfigRecord>();

        cameraConfig.HasKey(record => record.Id);

        cameraConfig.Property(record => record.Id)
            .HasMaxLength(64)
            .IsRequired();

        cameraConfig.Property(record => record.Name)
            .HasMaxLength(200)
            .IsRequired();

        cameraConfig.Property(record => record.RtspUrl)
            .HasMaxLength(2048)
            .IsRequired();

        var qrScan = modelBuilder.Entity<QrScanRecord>();

        qrScan.Property(record => record.CameraId)
            .HasMaxLength(64)
            .IsRequired();

        qrScan.Property(record => record.Value)
            .HasMaxLength(2048)
            .IsRequired();

        qrScan.HasIndex(record => new { record.CameraId, record.DetectedAt });
    }
}
