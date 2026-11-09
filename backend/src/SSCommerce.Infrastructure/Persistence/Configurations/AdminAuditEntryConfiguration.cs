using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SSCommerce.Domain.Administration;
using SSCommerce.Infrastructure.Identity;

namespace SSCommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>admin_audit_log</c> (Faz 8, docs/CONTRACT_PHASE_8.md "Persistence" 2). Yalnız eklenir: migration'daki trigger'lar UPDATE/DELETE/TRUNCATE'i
/// 23001 ile reddeder. Zaman uygulama saatinden gelir (DB varsayılanı yok). Hiçbir FK CASCADE değildir: işlemi yapan kullanıcı silinemez.
/// </summary>
internal sealed class AdminAuditEntryConfiguration : IEntityTypeConfiguration<AdminAuditEntry>
{
    public void Configure(EntityTypeBuilder<AdminAuditEntry> builder)
    {
        var targetTypes = string.Join(", ", AdminAuditRules.TargetTypes.Select(t => $"'{t}'"));
        var forbiddenKeys = string.Join(", ", AdminAuditRules.ForbiddenChangeKeys.Select(k => $"'{k}'"));

        builder.ToTable("admin_audit_log", t =>
        {
            t.HasCheckConstraint("ck_admin_audit_log_action_format", $"action ~ '{AdminAuditRules.ActionPattern}'");
            t.HasCheckConstraint("ck_admin_audit_log_target_type", $"target_type IN ({targetTypes})");
            t.HasCheckConstraint(
                "ck_admin_audit_log_changes_object",
                $"jsonb_typeof(changes) = 'object' AND octet_length(changes::text) <= {AdminAuditRules.ChangesMaxBytes} AND NOT (changes ?| ARRAY[{forbiddenKeys}])");
        });

        builder.HasKey(e => e.Id).HasName("pk_admin_audit_log");
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(AdminAuditRules.ActionMaxLength).IsRequired();
        builder.Property(e => e.TargetType).HasColumnName("target_type").HasMaxLength(32).IsRequired();
        builder.Property(e => e.TargetId).HasColumnName("target_id");
        builder.Property(e => e.Changes).HasColumnName("changes").HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");

        builder.HasIndex(e => e.ActorUserId).HasDatabaseName("ix_admin_audit_log_actor_user_id");
        // Genel akış (en yeni önce) ve hedef geçmişi (hedef başına en yeni önce); LIMIT sorguları sıralamadan okur.
        builder.HasIndex(e => new { e.OccurredAt, e.Id })
            .IsDescending(true, true)
            .HasDatabaseName("ix_admin_audit_log_occurred_at_id");
        builder.HasIndex(e => new { e.TargetType, e.TargetId, e.OccurredAt, e.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("ix_admin_audit_log_target_type_target_id_occurred_at_id");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(e => e.ActorUserId)
            .HasConstraintName("fk_admin_audit_log_actor_user")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
