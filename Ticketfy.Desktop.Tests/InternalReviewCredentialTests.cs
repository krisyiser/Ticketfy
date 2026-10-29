using Microsoft.EntityFrameworkCore;
using Ticketfy.Services.Implementations;

namespace Ticketfy.Desktop.Tests;

public sealed partial class EnterpriseCredentialBoundaryTests
{
    [Theory]
    [InlineData("R7m!a9")]
    [InlineData("Ár7!o9")]
    [InlineData("\U0001F332\U0001F33Ea7!b")]
    public async Task Review_SixCharacterPasswordCanCreateAuthenticateAndRotateAnAccount(string password)
    {
        await using var db = Db();
        var service = new UserService(db, operations: _actor);
        string id = Guid.NewGuid().ToString();
        await service.SaveAsync(id, "Operador de pruebas", "six-character", "CAJERO", password, null);
        Assert.NotNull((await service.SignInAsync("six-character", password)).User);
        await service.SaveAsync(id, "Operador de pruebas", "six-character", "CAJERO", "M4r!x8", null);
        Assert.NotNull((await service.SignInAsync("six-character", "M4r!x8")).User);
        Assert.Null((await service.SignInAsync("six-character", password)).User);
        Assert.Equal(2, await db.Users.CountAsync());
    }

    [Theory]
    [InlineData("R7m!a")]
    [InlineData("123456")]
    public async Task Review_TooShortOrObviousPasswordCannotCreateAccount(string password)
    {
        await using var db = Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UserService(db, operations: _actor)
            .SaveAsync(Guid.NewGuid().ToString(), "Prueba", "six-character", "CAJERO", password, null));
        Assert.Single(await db.Users.ToListAsync());
    }
}
