using Microsoft.EntityFrameworkCore;

namespace SSCommerce.Infrastructure.Persistence;

/// <summary>
/// Faz 1: entity ve migration yok (DECISIONS D-008). İlk model Faz 3'te Catalog ile gelir.
/// </summary>
public sealed class SSCommerceDbContext(DbContextOptions<SSCommerceDbContext> options) : DbContext(options);
