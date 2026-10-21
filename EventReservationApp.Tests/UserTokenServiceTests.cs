using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using EventReservationApp.Services.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EventReservationApp.Tests;

/// <summary>
/// The user token is the only thing that tells the internal agent API which
/// user a Rasa action is acting for, so only tokens this app signed, for this
/// purpose and still within their lifetime, may yield a user id.
/// </summary>
public class UserTokenServiceTests
{
    private static string NewPrivateKeyPem()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ecdsa.ExportPkcs8PrivateKeyPem();
    }

    private static UserTokenService CreateService(string privateKeyPem)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UserToken:PrivateKeyPem"] = privateKeyPem
            })
            .Build();

        return new UserTokenService(configuration);
    }

    private static string SignToken(
        string privateKeyPem,
        string subject,
        string issuer = UserTokenService.Issuer,
        string audience = UserTokenService.Audience,
        DateTime? expires = null,
        string? messageId = "message-1",
        string? correlationId = "correlation-1")
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var expiresAt = expires ?? DateTime.UtcNow.AddMinutes(5);

        var subjectClaims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, subject) };
        if (messageId is not null)
        {
            subjectClaims.Add(new Claim(UserTokenService.MessageIdClaim, messageId));
        }

        var claims = new Dictionary<string, object>();
        if (correlationId is not null)
        {
            claims[UserTokenService.CorrelationIdClaim] = correlationId;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(subjectClaims),
            Claims = claims,
            Issuer = issuer,
            Audience = audience,
            IssuedAt = expiresAt.AddMinutes(-10),
            NotBefore = expiresAt.AddMinutes(-10),
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256)
        });
    }

    [Fact]
    public async Task CreatedToken_ValidatesToTheSameUserIdAndCorrelationId()
    {
        var service = CreateService(NewPrivateKeyPem());

        var token = service.CreateToken("user-123", [], "correlation-1");

        var claims = await service.ValidateTokenAsync(token);
        Assert.NotNull(claims);
        Assert.Equal("user-123", claims.UserId);
        Assert.Equal("correlation-1", claims.CorrelationId);
        Assert.False(string.IsNullOrWhiteSpace(claims.MessageId));
    }

    // The message id identifies one chat message, so every token gets its own.
    [Fact]
    public async Task EveryCreatedToken_HasItsOwnMessageId()
    {
        var service = CreateService(NewPrivateKeyPem());

        var first = await service.ValidateTokenAsync(service.CreateToken("user-123", [], "correlation-1"));
        var second = await service.ValidateTokenAsync(service.CreateToken("user-123", [], "correlation-1"));

        Assert.NotEqual(first!.MessageId, second!.MessageId);
    }

    [Theory]
    [InlineData(null, "correlation-1")]
    [InlineData("message-1", null)]
    [InlineData("message-1", "")]
    public async Task TokenWithoutMessageOrCorrelationId_IsRejected(string? messageId, string? correlationId)
    {
        var key = NewPrivateKeyPem();
        var service = CreateService(key);

        var token = SignToken(key, "user-123", messageId: messageId, correlationId: correlationId);

        Assert.Null(await service.ValidateTokenAsync(token));
    }

    [Fact]
    public async Task CompleteTokenSignedWithTheAppKey_IsAccepted()
    {
        // Guards the rejection tests above: the same helper with all claims passes.
        var key = NewPrivateKeyPem();
        var service = CreateService(key);

        Assert.NotNull(await service.ValidateTokenAsync(SignToken(key, "user-123")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateToken_WithoutCorrelationId_Throws(string correlationId)
    {
        var service = CreateService(NewPrivateKeyPem());

        Assert.Throws<ArgumentException>(() => service.CreateToken("user-123", [], correlationId));
    }

    // Rasa limits knowledge base search by these roles, and reads the claim as a list.
    [Theory]
    [InlineData("")]
    [InlineData("Customer")]
    [InlineData("Administrator,Customer")]
    public void CreatedToken_CarriesRolesAsJsonArray(string roleList)
    {
        var roles = roleList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var service = CreateService(NewPrivateKeyPem());

        var token = service.CreateToken("user-123", roles, "correlation-1");

        var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]));
        var claim = payload.RootElement.GetProperty(UserTokenService.RolesClaim);
        Assert.Equal(JsonValueKind.Array, claim.ValueKind);
        Assert.Equal(roles, claim.EnumerateArray().Select(r => r.GetString()).ToArray());
    }

    [Fact]
    public async Task TokenSignedWithAnotherKey_IsRejected()
    {
        var service = CreateService(NewPrivateKeyPem());

        var forged = SignToken(NewPrivateKeyPem(), "victim");

        Assert.Null(await service.ValidateTokenAsync(forged));
    }

    [Fact]
    public async Task TamperedToken_IsRejected()
    {
        var key = NewPrivateKeyPem();
        var service = CreateService(key);
        var token = service.CreateToken("attacker", [], "correlation-1");

        // Swap the payload for one naming another user, keeping the original signature.
        var parts = token.Split('.');
        var victimPayload = Base64UrlEncoder.Encode(
            Base64UrlEncoder.Decode(parts[1]).Replace("\"attacker\"", "\"victim\""));
        var tampered = $"{parts[0]}.{victimPayload}.{parts[2]}";

        Assert.Null(await service.ValidateTokenAsync(tampered));
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var key = NewPrivateKeyPem();
        var service = CreateService(key);

        var expired = SignToken(key, "user-123", expires: DateTime.UtcNow.AddMinutes(-2));

        Assert.Null(await service.ValidateTokenAsync(expired));
    }

    [Theory]
    [InlineData("some-other-app", UserTokenService.Audience)]
    [InlineData(UserTokenService.Issuer, "some-other-api")]
    public async Task TokenForAnotherIssuerOrAudience_IsRejected(string issuer, string audience)
    {
        var key = NewPrivateKeyPem();
        var service = CreateService(key);

        var token = SignToken(key, "user-123", issuer, audience);

        Assert.Null(await service.ValidateTokenAsync(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public async Task MalformedToken_IsRejected(string token)
    {
        var service = CreateService(NewPrivateKeyPem());

        Assert.Null(await service.ValidateTokenAsync(token));
    }

    [Fact]
    public void MissingPrivateKey_FailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => CreateService(""));
    }
}
