using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ticketfy.Data.Entities;

/// <summary>
/// Customer entity with debt tracking, credit limit validation, and loyalty points.
/// Maps to legacy 'customers' SQLite table.
/// </summary>
[Table("customers")]
public class CustomerEntity
{
    public bool IsActive { get; set; } = true;
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Required]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [NotMapped]
    public string FullName => Name;

    [Column("phone")]
    public string Phone { get; set; } = string.Empty;

    [Column("customer_code")]
    public string CustomerCode { get; set; } = string.Empty;

    [Column("rfc")]
    public string Rfc { get; set; } = string.Empty;

    [Column("email")]
    public string Email { get; set; } = string.Empty;

    [Column("address")]
    public string Address { get; set; } = string.Empty;

    [Column("credit_limit")]
    public decimal CreditLimit { get; set; } = 5000.0m;

    [Column("debt")]
    public decimal Debt { get; set; }

    [Column("is_credit_blocked")]
    public bool IsCreditBlocked { get; set; } = false;

    [Column("puntos_saldo")]
    public double PuntosSaldo { get; set; }

    [NotMapped]
    public decimal CurrentDebt
    {
        get => Debt;
        set => Debt = value;
    }

    [NotMapped]
    public decimal CreditLimitDecimal
    {
        get => CreditLimit;
        set => CreditLimit = value;
    }

    [NotMapped]
    public decimal AvailableCredit => Math.Max(0m, CreditLimit - Debt);

    /// <summary>Navigation: payments made by this customer.</summary>
    public ICollection<CustomerPaymentEntity> Payments { get; set; } = [];

    /// <summary>Navigation: optional fiscal data link.</summary>
    public FiscalClientEntity? FiscalData { get; set; }
}
