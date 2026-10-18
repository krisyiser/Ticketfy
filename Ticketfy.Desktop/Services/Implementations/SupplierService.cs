using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Ticketfy.Data;
using Ticketfy.Data.Dtos;
using Ticketfy.Data.Entities;
using Ticketfy.Services.Interfaces;

namespace Ticketfy.Services.Implementations;

public class SupplierService : ISupplierService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly CommercialOperationContext? _operations;

    public SupplierService(AppDbContext context, CommercialOperationContext? operations = null)
        : this(new AppDbContextFactoryImpl<AppDbContext>(context.Options), operations) { }
    public SupplierService(IDbContextFactory<AppDbContext> factory, CommercialOperationContext? operations = null)
    { _factory = factory; _operations = operations; }

    public async Task<List<SupplierDto>> GetAllAsync()
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var entities = await _context.Suppliers.AsNoTracking().Where(s => s.IsActive == 1).ToListAsync();
        return entities.Select(MapToDto).ToList();
    }

    public async Task<SupplierDto?> GetByIdAsync(string id)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var entity = await _context.Suppliers.FindAsync(id);
        return entity == null ? null : MapToDto(entity);
    }

    public async Task<SupplierDto> CreateAsync(SupplierDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new InvalidOperationException("Nombre de proveedor requerido.");
        await using var _context = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(_context, Ticketfy.Core.Security.PermissionCatalog.SuppliersManage, false);
        var entity = new SupplierEntity
        {
            Id = string.IsNullOrEmpty(dto.Id) ? System.Guid.NewGuid().ToString() : dto.Id,
            Name = dto.Name,
            Rfc = dto.Rfc,
            Phone = dto.Phone,
            Email = dto.Email,
            Address = dto.Address,
            ContactPerson = dto.ContactPerson,
            IsActive = dto.IsActive ? 1 : 0
        };

        _context.Suppliers.Add(entity);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "SUPPLIER_CREATED", entity.Id, 0, "Alta de proveedor");
        await _context.SaveChangesAsync();
        return MapToDto(entity);
    }

    public async Task<SupplierDto> UpdateAsync(SupplierDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new InvalidOperationException("Nombre de proveedor requerido.");
        await using var _context = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(_context, Ticketfy.Core.Security.PermissionCatalog.SuppliersManage, false);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "SUPPLIER_UPDATEASYNC", dto.Id, 0, "Administración de proveedores");
        var entity = await _context.Suppliers.FindAsync(dto.Id);
        if (entity != null)
        {
            entity.Name = dto.Name;
            entity.Rfc = dto.Rfc;
            entity.Phone = dto.Phone;
            entity.Email = dto.Email;
            entity.Address = dto.Address;
            entity.ContactPerson = dto.ContactPerson;
            entity.IsActive = dto.IsActive ? 1 : 0;
            await _context.SaveChangesAsync();
        }
        return dto;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await using var _context = await _factory.CreateDbContextAsync();
        var actor = await (_operations ?? throw new UnauthorizedAccessException("Se requiere una sesión autenticada.")).RequireAsync(_context, Ticketfy.Core.Security.PermissionCatalog.SuppliersManage, false);
        CommercialLedger.Journal(_context, actor, Guid.NewGuid().ToString("N"), "SUPPLIER_DELETEASYNC", id, 0, "Administración de proveedores");
        var entity = await _context.Suppliers.FindAsync(id);
        if (entity != null)
        {
            entity.IsActive = 0;
            await _context.SaveChangesAsync();
            return true;
        }
        return false;
    }

    private static SupplierDto MapToDto(SupplierEntity e) =>
        new(e.Id, e.Name, e.Rfc, e.Phone, e.Email, e.Address, e.ContactPerson, e.IsActive == 1);
}
