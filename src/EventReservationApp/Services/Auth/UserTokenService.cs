using System.Security.Claims;
using System.Security.Cryptography;
using EventReservationApp.Services.Interfaces;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EventReservationApp.Services.Auth;

public class UserTokenService : IUserTokenService
{
    // Must match USER_TOKEN_ISSUER / USER_TOKEN_AUDIENCE in the Rasa project
    // (secure_rest_channel.py).
    public const string Issuer = "event-reservation-app";
    public const string Audience = "event-reservation-chatbot";

    // Must match the claim names read in user_token.py / secure_rest_channel.py.
    public const string RolesClaim = "roles";
    public const string MessageIdClaim = JwtRegisteredClaimNames.Jti;
    public const string CorrelationIdClaim = "cid";

    // One token per chat message. Short, because the token is stored in Rasa's
    // tracker store with the message metadata and must not stay usable there.
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly JsonWebTokenHandler _tokenHandler = new();
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;

    public UserTokenService(IConfiguration configuration)
    {
        var privateKeyPem = configuration["UserToken:PrivateKeyPem"];
        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException("UserToken:PrivateKeyPem is not configured.");
        }

        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        if (ecdsa.KeySize != 256)
        {
            throw new InvalidOperationException("UserToken:PrivateKeyPem must be an EC P-256 private key.");
        }

        var key = new ECDsaSecurityKey(ecdsa);
        _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);

        _validationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = key,
            ValidAlgorithms = new[] { SecurityAlgorithms.EcdsaSha256 },
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    public string CreateToken(string userId, IEnumerable<string> roles, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        }

        ArgumentNullException.ThrowIfNull(roles);

        var now = DateTime.UtcNow;

        return _tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                // New for every token, i.e. for every chat message.
                new Claim(MessageIdClaim, Guid.NewGuid().ToString())
            }),
            Claims = new Dictionary<string, object>
            {
                [RolesClaim] = roles.ToArray(),
                [CorrelationIdClaim] = correlationId
            },
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(Lifetime),
            SigningCredentials = _signingCredentials
        });
    }

    public async Task<UserTokenClaims?> ValidateTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = await _tokenHandler.ValidateTokenAsync(token, _validationParameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        jwt.TryGetPayloadValue<string>(CorrelationIdClaim, out var correlationId);
        if (string.IsNullOrWhiteSpace(jwt.Subject) ||
            string.IsNullOrWhiteSpace(jwt.Id) ||
            string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        return new UserTokenClaims(jwt.Subject, jwt.Id, correlationId);
    }
}
