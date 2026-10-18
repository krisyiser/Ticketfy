using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Ticketfy.Core.Enums;

namespace Ticketfy.Data.Entities;

/// <summary>
/// System user entity with secure credentials and RBAC mapping.
/// Maps to legacy 'usuarios' SQLite table.
/// </summary>
[Table("usuarios")]
public partial class UserEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column("nombre")]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [Column("username")]
    public string Username { get; set; } = string.Empty;

    [Column("password_hash")]
    public string PasswordHash { get; set; } = string.Empty;

    [Column("password_salt")]
    public byte[]? PasswordSalt { get; set; }

    [Column("password_hash_bytes")]
    public byte[]? PasswordHashBytes { get; set; }

    [Column("password_hint")]
    public string PasswordHint { get; set; } = string.Empty; // Legacy column; cleared and no longer exposed.

    [Column("pin_checador_hash")]
    public string PinCode { get; set; } = string.Empty;      // PBKDF2 hash of the 4-digit PIN.

    [Column("rol")]
    public UserRole Role { get; set; } = UserRole.Cajero;

    [Column("role_string")]
    public string RoleString { get; set; } = string.Empty;

    [Column("estatus")]
    public bool IsActive { get; set; } = true;

    /// <summary>Navigation: attendance records for this user.</summary>
    public ICollection<AttendanceEntity> Attendances { get; set; } = [];
}
